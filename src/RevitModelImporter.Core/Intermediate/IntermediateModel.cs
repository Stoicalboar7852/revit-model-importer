using System.Text.Json.Serialization;
using RevitModelImporter.Core.Processing;

namespace RevitModelImporter.Core.Intermediate;

/// <summary>JSON header of an intermediate (.rmi) file. Geometry lives in binary buffers after the header.
/// All lengths are Revit internal units (decimal feet), Z up, world space.</summary>
public sealed class IntermediateHeader
{
    public const string FormatName = "RevitModelImporter.Intermediate";

    /// <summary>Major version of the header schema. Readers refuse newer major versions.</summary>
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = CurrentVersion;

    public string Generator { get; set; } = "";

    public SourceInfo Source { get; set; } = new();

    public string LengthUnit { get; set; } = "feet";

    public string UpAxis { get; set; } = "Z";

    public MaterialMode MaterialMode { get; set; }

    public List<IntermediateMaterial> Materials { get; set; } = [];

    public List<IntermediateObject> Objects { get; set; } = [];

    public List<BufferView> Buffers { get; set; } = [];

    public double[] BoundsMin { get; set; } = [0, 0, 0];

    public double[] BoundsMax { get; set; } = [0, 0, 0];

    public ConversionStats Stats { get; set; } = new();

    public List<string> Warnings { get; set; } = [];
}

public sealed class SourceInfo
{
    public string Path { get; set; } = "";

    public string FormatId { get; set; } = "";

    public long SizeBytes { get; set; }

    public DateTime LastWriteUtc { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<MaterialKind>))]
public enum MaterialKind
{
    /// <summary>Converted from a source material (textured or flat).</summary>
    Source,

    /// <summary>A palette colour produced by texture baking.</summary>
    Baked,
}

public sealed class IntermediateMaterial
{
    public string Name { get; set; } = "";

    /// <summary>Stable signature used to reuse identical materials on re-import.</summary>
    public string Key { get; set; } = "";

    public MaterialKind Kind { get; set; }

    /// <summary>sRGB colour, 3 bytes.</summary>
    public byte[] Color { get; set; } = [204, 204, 204];

    /// <summary>0 = fully transparent, 1 = opaque.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Absolute path of the diffuse texture in the destination texture folder, if any.</summary>
    public string? DiffuseTexture { get; set; }

    /// <summary>Real-world size of one texture repeat along U, in feet.</summary>
    public double TextureSizeX { get; set; }

    /// <summary>Real-world size of one texture repeat along V, in feet.</summary>
    public double TextureSizeY { get; set; }

    public string? BumpTexture { get; set; }

    public double BumpAmount { get; set; }

    public bool IsMetal { get; set; }

    /// <summary>0..1, from 1 − roughness.</summary>
    public double Glossiness { get; set; } = 0.2;

    /// <summary>Number of triangles that use this material (after processing).</summary>
    public long TriangleCount { get; set; }
}

public sealed class IntermediateObject
{
    public string Name { get; set; } = "";

    public string NodePath { get; set; } = "";

    public int VertexCount { get; set; }

    public int TriangleCount { get; set; }

    /// <summary>Buffer index of float64 xyz positions.</summary>
    public int Positions { get; set; }

    /// <summary>Buffer index of int32 triangle indices.</summary>
    public int Indices { get; set; }

    /// <summary>Buffer index of int32 per-triangle material indices.</summary>
    public int TriangleMaterials { get; set; }
}

public sealed class BufferView
{
    /// <summary>Byte offset from the start of the binary blob.</summary>
    public long Offset { get; set; }

    public long Length { get; set; }

    /// <summary>"f64" or "i32".</summary>
    public string Type { get; set; } = "";
}

/// <summary>Numbers reported back to the user in the import summary.</summary>
public sealed class ConversionStats
{
    public long SourceTriangles { get; set; }

    public long TrianglesBeforeSimplify { get; set; }

    public long TrianglesAfterSimplify { get; set; }

    public bool Simplified { get; set; }

    public long OutputTriangles { get; set; }

    public long OutputVertices { get; set; }

    public long WeldedVertices { get; set; }

    public long DegenerateTrianglesRemoved { get; set; }

    public long DuplicateTrianglesRemoved { get; set; }

    public long ShortEdgeTrianglesRemoved { get; set; }

    public int TexturesWritten { get; set; }

    public List<string> MissingTextures { get; set; } = [];

    public List<string> UnsupportedTextures { get; set; } = [];

    public string? TextureFolder { get; set; }

    public int PaletteSize { get; set; }

    public Dictionary<string, long> StageMilliseconds { get; set; } = [];
}

/// <summary>Geometry of one object as read back from an intermediate file.</summary>
public sealed class IntermediateMesh
{
    public required IntermediateObject Object { get; init; }

    public required double[] Positions { get; init; }

    public required int[] Indices { get; init; }

    public required int[] TriangleMaterials { get; init; }
}

public sealed class IntermediateModel
{
    public required IntermediateHeader Header { get; init; }

    public required IReadOnlyList<IntermediateMesh> Meshes { get; init; }
}
