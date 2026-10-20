using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RevitModelImporter.Core.Scene;

namespace RevitModelImporter.Core.Textures;

/// <summary>Minimal RGBA8 PNG encoder (zlib from the BCL), so converted textures need no image library.</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Write(string path, RgbaImage image)
    {
        using var fs = File.Create(path);
        Write(fs, image);
    }

    public static byte[] Encode(RgbaImage image)
    {
        using var ms = new MemoryStream();
        Write(ms, image);
        return ms.ToArray();
    }

    public static void Write(Stream output, RgbaImage image)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), image.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // colour type RGBA
        WriteChunk(output, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var stride = image.Width * 4;
                var prev = new byte[stride];
                var line = new byte[stride + 1];
                for (var y = 0; y < image.Height; y++)
                {
                    var row = image.Pixels.AsSpan(y * stride, stride);
                    // Filter 4 (Paeth) compresses photos and gradients well and is cheap.
                    line[0] = 4;
                    for (var x = 0; x < stride; x++)
                    {
                        var a = x >= 4 ? row[x - 4] : (byte)0;
                        var b = prev[x];
                        var c = x >= 4 ? prev[x - 4] : (byte)0;
                        line[x + 1] = (byte)(row[x] - Paeth(a, b, c));
                    }

                    z.Write(line);
                    row.CopyTo(prev);
                }
            }

            WriteChunk(output, "IDAT", raw.ToArray());
        }

        WriteChunk(output, "IEND", []);
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
