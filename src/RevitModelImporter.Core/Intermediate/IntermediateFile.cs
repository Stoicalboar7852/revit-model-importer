using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RevitModelImporter.Core.Processing;

namespace RevitModelImporter.Core.Intermediate;

/// <summary>Reads and writes the intermediate file shared by the worker and the add-in.
/// Layout: "RMI1" magic, uint32 container version, uint64 header length, UTF-8 JSON header,
/// zero padding to 8 bytes, then the binary blob (little-endian buffers).</summary>
public static class IntermediateFile
{
    public const string Extension = ".rmi";
    private const uint ContainerVersion = 1;
    private static readonly byte[] Magic = "RMI1"u8.ToArray();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes <paramref name="meshes"/> (already in Revit units) and fills in the header's objects,
    /// buffers and bounds.</summary>
    public static void Write(string path, IntermediateHeader header, IReadOnlyList<ObjectMesh> meshes)
    {
        header.Objects = [];
        header.Buffers = [];
        var bounds = new Geometry.BoundingBox();
        long offset = 0;
        foreach (var m in meshes)
        {
            bounds.Add(m.ComputeBounds());
            var obj = new IntermediateObject
            {
                Name = m.Name,
                NodePath = m.NodePath,
                VertexCount = m.VertexCount,
                TriangleCount = m.TriangleCount,
                Positions = AddBuffer(header, ref offset, m.Positions.Length * 8L, "f64"),
                Indices = AddBuffer(header, ref offset, m.Indices.Length * 4L, "i32"),
                TriangleMaterials = AddBuffer(header, ref offset, m.TriangleMaterials.Length * 4L, "i32"),
            };
            header.Objects.Add(obj);
        }

        if (!bounds.IsEmpty)
        {
            header.BoundsMin = [bounds.Min.X, bounds.Min.Y, bounds.Min.Z];
            header.BoundsMax = [bounds.Max.X, bounds.Max.Y, bounds.Max.Z];
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Span<byte> prefix = stackalloc byte[16];
            Magic.CopyTo(prefix);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix[4..], ContainerVersion);
            BinaryPrimitives.WriteUInt64LittleEndian(prefix[8..], (ulong)json.Length);
            fs.Write(prefix);
            fs.Write(json);
            var pad = (int)(Align8(16 + json.Length) - (16 + json.Length));
            fs.Write(new byte[pad]);

            foreach (var m in meshes)
            {
                WriteSpan<double>(fs, m.Positions);
                WriteSpan<int>(fs, m.Indices);
                WriteSpan<int>(fs, m.TriangleMaterials);
            }
        }

        File.Move(tmp, path, overwrite: true);
    }

    public static IntermediateHeader ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadHeader(fs, out _);
    }

    public static IntermediateModel Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var header = ReadHeader(fs, out var blobStart);
        var meshes = new List<IntermediateMesh>(header.Objects.Count);
        foreach (var obj in header.Objects)
        {
            meshes.Add(new IntermediateMesh
            {
                Object = obj,
                Positions = ReadBuffer<double>(fs, blobStart, header, obj.Positions, "f64"),
                Indices = ReadBuffer<int>(fs, blobStart, header, obj.Indices, "i32"),
                TriangleMaterials = ReadBuffer<int>(fs, blobStart, header, obj.TriangleMaterials, "i32"),
            });
            Validate(header, meshes[^1]);
        }

        return new IntermediateModel { Header = header, Meshes = meshes };
    }

    private static IntermediateHeader ReadHeader(Stream fs, out long blobStart)
    {
        Span<byte> prefix = stackalloc byte[16];
        if (fs.ReadAtLeast(prefix, 16, throwOnEndOfStream: false) < 16 || !prefix[..4].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a Revit Model Importer intermediate file.");
        }

        var container = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);
        if (container != ContainerVersion)
        {
            throw new InvalidDataException($"Unsupported intermediate container version {container}; this add-in reads version {ContainerVersion}. Make sure the add-in and worker come from the same release.");
        }

        var length = BinaryPrimitives.ReadUInt64LittleEndian(prefix[8..]);
        if (length > int.MaxValue || (long)length > fs.Length - 16)
        {
            throw new InvalidDataException("Intermediate file header is truncated.");
        }

        var json = new byte[(int)length];
        fs.ReadExactly(json);
        var header = JsonSerializer.Deserialize<IntermediateHeader>(json, JsonOptions)
            ?? throw new InvalidDataException("Intermediate file header is empty.");
        if (header.Format != IntermediateHeader.FormatName)
        {
            throw new InvalidDataException($"Unexpected intermediate format '{header.Format}'.");
        }

        if (header.Version > IntermediateHeader.CurrentVersion)
        {
            throw new InvalidDataException($"Intermediate file version {header.Version} is newer than this add-in supports ({IntermediateHeader.CurrentVersion}). Make sure the add-in and worker come from the same release.");
        }

        blobStart = Align8(16 + json.Length);
        return header;
    }

    private static int AddBuffer(IntermediateHeader header, ref long offset, long length, string type)
    {
        header.Buffers.Add(new BufferView { Offset = offset, Length = length, Type = type });
        offset += length;
        return header.Buffers.Count - 1;
    }

    private static T[] ReadBuffer<T>(Stream fs, long blobStart, IntermediateHeader header, int index, string type)
        where T : unmanaged
    {
        if (index < 0 || index >= header.Buffers.Count)
        {
            throw new InvalidDataException($"Buffer index {index} is out of range.");
        }

        var view = header.Buffers[index];
        var size = Marshal.SizeOf<T>();
        if (view.Type != type || view.Length % size != 0 || view.Offset < 0 || blobStart + view.Offset + view.Length > fs.Length)
        {
            throw new InvalidDataException($"Buffer {index} is corrupt.");
        }

        var result = new T[view.Length / size];
        fs.Seek(blobStart + view.Offset, SeekOrigin.Begin);
        fs.ReadExactly(MemoryMarshal.AsBytes(result.AsSpan()));
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Big-endian platforms are not supported.");
        }

        return result;
    }

    private static void Validate(IntermediateHeader header, IntermediateMesh mesh)
    {
        var vertexCount = mesh.Positions.Length / 3;
        if (mesh.Indices.Length != mesh.TriangleMaterials.Length * 3)
        {
            throw new InvalidDataException($"Object '{mesh.Object.Name}' has mismatched index and material buffers.");
        }

        foreach (var i in mesh.Indices)
        {
            if ((uint)i >= (uint)vertexCount)
            {
                throw new InvalidDataException($"Object '{mesh.Object.Name}' references a vertex that does not exist.");
            }
        }

        foreach (var m in mesh.TriangleMaterials)
        {
            if ((uint)m >= (uint)header.Materials.Count)
            {
                throw new InvalidDataException($"Object '{mesh.Object.Name}' references a material that does not exist.");
            }
        }
    }

    private static void WriteSpan<T>(Stream s, T[] data)
        where T : unmanaged
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Big-endian platforms are not supported.");
        }

        s.Write(MemoryMarshal.AsBytes(data.AsSpan()));
    }

    private static long Align8(long value) => (value + 7) & ~7L;

    /// <summary>Utility for tests and diagnostics.</summary>
    public static string HeaderJson(IntermediateHeader header) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions));
}
