using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Units;

namespace RevitModelImporter.Core.Scene;

/// <summary>Format-neutral scene as loaded from a source file. Geometry is in source units and source axes;
/// node transforms are local to the parent.</summary>
public sealed class SceneModel
{
    public string SourcePath { get; set; } = "";

    /// <summary>Lower-case extension without the dot, e.g. "obj", "glb".</summary>
    public string FormatId { get; set; } = "";

    public List<SceneMesh> Meshes { get; } = [];

    public List<SceneMaterial> Materials { get; } = [];

    public List<SceneTexture> Textures { get; } = [];

    public SceneNode Root { get; set; } = new() { Name = "Root" };

    /// <summary>Unit stated by the file, or null when the format does not say.</summary>
    public LengthUnit? DetectedUnit { get; set; }

    /// <summary>Where <see cref="DetectedUnit"/> came from, for display ("glTF is always meters").</summary>
    public string? UnitSource { get; set; }

    public UpAxis DefaultUpAxis { get; set; } = UpAxis.YUp;

    public string? UpAxisSource { get; set; }

    /// <summary>Number of polygons in the file before triangulation (if the loader knows it).</summary>
    public long SourcePolygonCount { get; set; }

    public List<string> Warnings { get; } = [];
}

/// <summary>A triangle mesh with a single material, in the local space of the nodes that reference it.</summary>
public sealed class SceneMesh
{
    public string Name { get; set; } = "";

    /// <summary>xyz triples.</summary>
    public double[] Positions { get; set; } = [];

    /// <summary>uv pairs per vertex (first UV channel), or null.</summary>
    public float[]? TexCoords { get; set; }

    /// <summary>Triangle vertex indices (3 per triangle).</summary>
    public int[] Indices { get; set; } = [];

    public int MaterialIndex { get; set; }

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;
}

public sealed class SceneNode
{
    public string Name { get; set; } = "";

    public Mat4 LocalTransform { get; set; } = Mat4.Identity;

    public List<int> MeshIndices { get; } = [];

    public List<SceneNode> Children { get; } = [];

    public IEnumerable<SceneNode> DescendantsAndSelf()
    {
        var stack = new Stack<SceneNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (var i = n.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(n.Children[i]);
            }
        }
    }
}

/// <summary>Linear RGBA colour, components 0..1.</summary>
public readonly record struct ColorRgba(double R, double G, double B, double A = 1.0)
{
    public static readonly ColorRgba White = new(1, 1, 1, 1);

    public static ColorRgba FromSrgb(double r, double g, double b, double a = 1.0) =>
        new(ColorSpace.SrgbToLinear(r), ColorSpace.SrgbToLinear(g), ColorSpace.SrgbToLinear(b), a);

    public (byte R, byte G, byte B) ToSrgbBytes() =>
        (ColorSpace.LinearToSrgbByte(R), ColorSpace.LinearToSrgbByte(G), ColorSpace.LinearToSrgbByte(B));

    public ColorRgba Multiply(ColorRgba o) => new(R * o.R, G * o.G, B * o.B, A * o.A);

    public string ToHex()
    {
        var (r, g, b) = ToSrgbBytes();
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}

public static class ColorSpace
{
    private static readonly double[] SrgbByteToLinearTable = BuildTable();

    public static double SrgbToLinear(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static double LinearToSrgb(double c)
    {
        c = Math.Clamp(c, 0.0, 1.0);
        return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
    }

    public static byte LinearToSrgbByte(double c) => (byte)Math.Clamp((int)Math.Round(LinearToSrgb(c) * 255.0), 0, 255);

    public static double SrgbByteToLinear(byte b) => SrgbByteToLinearTable[b];

    private static double[] BuildTable()
    {
        var t = new double[256];
        for (var i = 0; i < 256; i++)
        {
            t[i] = SrgbToLinear(i / 255.0);
        }

        return t;
    }
}

public sealed class SceneMaterial
{
    public string Name { get; set; } = "";

    /// <summary>Base/diffuse colour in linear space; alpha is opacity.</summary>
    public ColorRgba BaseColor { get; set; } = new(0.8, 0.8, 0.8, 1);

    public TextureRef? BaseColorTexture { get; set; }

    public TextureRef? NormalTexture { get; set; }

    public TextureRef? BumpTexture { get; set; }

    public double Metallic { get; set; }

    public double Roughness { get; set; } = 0.8;

    public bool IsPbr { get; set; }

    public bool TwoSided { get; set; }
}

public sealed record TextureRef(int TextureIndex, int UvChannel = 0);

/// <summary>A texture referenced by the scene: an external file or an embedded image.</summary>
public sealed class SceneTexture
{
    /// <summary>The path or reference exactly as written in the source file (e.g. "textures\\Brick.PNG" or "*0").</summary>
    public string OriginalReference { get; set; } = "";

    /// <summary>Resolved file on disk for external textures; null when missing or embedded.</summary>
    public string? ResolvedPath { get; set; }

    /// <summary>Compressed image bytes (PNG/JPG/…) for embedded textures.</summary>
    public byte[]? EmbeddedData { get; set; }

    /// <summary>Format hint for embedded data ("png", "jpg"), when known.</summary>
    public string? EmbeddedFormatHint { get; set; }

    /// <summary>Uncompressed embedded pixels (rare; some FBX/X files).</summary>
    public RgbaImage? EmbeddedPixels { get; set; }

    public bool IsEmbedded => EmbeddedData is not null || EmbeddedPixels is not null;

    public bool IsMissing => !IsEmbedded && ResolvedPath is null;

    /// <summary>Short display name for warnings and file names.</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(OriginalReference) && !OriginalReference.StartsWith('*'))
            {
                var name = OriginalReference.Replace('\\', '/');
                var slash = name.LastIndexOf('/');
                return slash >= 0 ? name[(slash + 1)..] : name;
            }

            return string.IsNullOrEmpty(OriginalReference) ? "texture" : "embedded" + OriginalReference.TrimStart('*');
        }
    }
}

/// <summary>8-bit RGBA image, rows top to bottom.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        if (pixels.Length < (long)width * height * 4)
        {
            throw new ArgumentException("Pixel buffer is too small for the image size.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public static RgbaImage Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[width * height * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = a;
        }

        return new RgbaImage(width, height, px);
    }
}
