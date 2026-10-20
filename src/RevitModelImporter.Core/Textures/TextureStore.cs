using System.Security.Cryptography;
using System.Text;
using RevitModelImporter.Core.Scene;

namespace RevitModelImporter.Core.Textures;

/// <summary>Reads, decodes, converts and exports the textures of one scene. Exported files are named
/// <c>&lt;name&gt;_&lt;content hash&gt;.&lt;ext&gt;</c> so re-imports reuse the same file.</summary>
public sealed class TextureStore
{
    private readonly SceneModel _scene;
    private readonly ITextureDecoder _decoder;
    private readonly string? _outputDirectory;
    private readonly int _samplingSize;
    private readonly Dictionary<int, byte[]?> _bytes = [];
    private readonly Dictionary<int, RgbaImage?> _images = [];
    private readonly Dictionary<int, TextureSampler?> _samplers = [];
    private readonly Dictionary<int, string?> _exported = [];

    public TextureStore(SceneModel scene, ITextureDecoder decoder, string? outputDirectory, int samplingSize = 1024)
    {
        _scene = scene;
        _decoder = decoder;
        _outputDirectory = outputDirectory;
        _samplingSize = samplingSize;
    }

    public SortedSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SortedSet<string> Unsupported { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Written { get; private set; }

    /// <summary>Compressed bytes of the texture (embedded or read from disk), or null if missing.</summary>
    public byte[]? GetBytes(int index)
    {
        if (index < 0 || index >= _scene.Textures.Count)
        {
            return null;
        }

        if (_bytes.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var t = _scene.Textures[index];
        byte[]? data = null;
        if (t.EmbeddedData is not null)
        {
            data = t.EmbeddedData;
        }
        else if (t.EmbeddedPixels is not null)
        {
            data = PngWriter.Encode(t.EmbeddedPixels);
        }
        else if (t.ResolvedPath is not null)
        {
            try
            {
                data = File.ReadAllBytes(t.ResolvedPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                data = null;
            }
        }

        if (data is null)
        {
            Missing.Add(t.OriginalReference);
        }

        _bytes[index] = data;
        return data;
    }

    /// <summary>Decoded pixels at full resolution, or null when missing or undecodable.</summary>
    public RgbaImage? GetImage(int index)
    {
        if (_images.TryGetValue(index, out var cached))
        {
            return cached;
        }

        RgbaImage? image = null;
        var t = index >= 0 && index < _scene.Textures.Count ? _scene.Textures[index] : null;
        if (t?.EmbeddedPixels is not null)
        {
            image = t.EmbeddedPixels;
        }
        else if (GetBytes(index) is { } data)
        {
            image = _decoder.TryDecode(data, t?.DisplayName, out _);
            if (image is null && t is not null)
            {
                Unsupported.Add(t.DisplayName);
            }
        }

        _images[index] = image;
        return image;
    }

    /// <summary>Sampler on a downscaled copy (for baking and average colours).</summary>
    public TextureSampler? GetSampler(int index)
    {
        if (_samplers.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var image = GetImage(index);
        var sampler = image is null ? null : new TextureSampler(ImageFormats.Downscale(image, _samplingSize));
        _samplers[index] = sampler;
        return sampler;
    }

    /// <summary>Writes the texture into the output folder in a format Revit renders and returns its full path,
    /// or null when there is no output folder or the texture is missing or cannot be converted.</summary>
    public string? Export(int index)
    {
        if (_outputDirectory is null)
        {
            return null;
        }

        if (_exported.TryGetValue(index, out var cached))
        {
            return cached;
        }

        string? path = null;
        var data = GetBytes(index);
        if (data is not null)
        {
            var t = _scene.Textures[index];
            var sniffed = ImageFormats.Sniff(data) ?? Path.GetExtension(t.DisplayName).TrimStart('.').ToLowerInvariant();
            var ext = sniffed switch
            {
                "jpeg" => "jpg",
                "tiff" => "tif",
                _ => sniffed,
            };
            var baseName = SafeName(Path.GetFileNameWithoutExtension(t.DisplayName));
            var hash = Convert.ToHexString(SHA256.HashData(data))[..12].ToLowerInvariant();
            Directory.CreateDirectory(_outputDirectory);

            if (ImageFormats.RevitNative.Contains(ext))
            {
                path = Path.Combine(_outputDirectory, $"{baseName}_{hash}.{ext}");
                if (!File.Exists(path))
                {
                    WriteAtomically(path, data);
                    Written++;
                }
            }
            else if (GetImage(index) is { } image)
            {
                path = Path.Combine(_outputDirectory, $"{baseName}_{hash}.png");
                if (!File.Exists(path))
                {
                    WriteAtomically(path, PngWriter.Encode(image));
                    Written++;
                }
            }
        }

        _exported[index] = path;
        return path;
    }

    private static void WriteAtomically(string path, byte[] data)
    {
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    internal static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        var s = sb.ToString().Trim('_');
        if (s.Length == 0)
        {
            s = "texture";
        }

        return s.Length > 40 ? s[..40] : s;
    }
}
