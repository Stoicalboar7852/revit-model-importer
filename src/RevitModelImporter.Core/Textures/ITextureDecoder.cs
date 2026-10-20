using RevitModelImporter.Core.Scene;

namespace RevitModelImporter.Core.Textures;

/// <summary>Decodes image files. Implemented in the worker (StbImageSharp, Pfim) so Core has no image dependency.</summary>
public interface ITextureDecoder
{
    /// <summary>Decodes <paramref name="data"/> to RGBA8, or returns null and an error message.</summary>
    RgbaImage? TryDecode(byte[] data, string? nameHint, out string? error);
}

public static class ImageFormats
{
    /// <summary>Extensions Revit's renderer reads directly; anything else is converted to PNG.</summary>
    public static readonly IReadOnlySet<string> RevitNative = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "bmp", "tif", "tiff",
    };

    /// <summary>Identifies an image by its magic bytes; returns a lower-case extension or null.</summary>
    public static string? Sniff(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
        {
            return "png";
        }

        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            return "jpg";
        }

        if (d.Length >= 2 && d[0] == (byte)'B' && d[1] == (byte)'M')
        {
            return "bmp";
        }

        if (d.Length >= 4 && ((d[0] == (byte)'I' && d[1] == (byte)'I' && d[2] == 42 && d[3] == 0) || (d[0] == (byte)'M' && d[1] == (byte)'M' && d[2] == 0 && d[3] == 42)))
        {
            return "tif";
        }

        if (d.Length >= 4 && d[0] == (byte)'D' && d[1] == (byte)'D' && d[2] == (byte)'S' && d[3] == (byte)' ')
        {
            return "dds";
        }

        if (d.Length >= 12 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F' && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P')
        {
            return "webp";
        }

        if (d.Length >= 12 && d[0] == 0xAB && d[1] == (byte)'K' && d[2] == (byte)'T' && d[3] == (byte)'X')
        {
            return "ktx2";
        }

        if (d.Length >= 4 && d[0] == (byte)'8' && d[1] == (byte)'B' && d[2] == (byte)'P' && d[3] == (byte)'S')
        {
            return "psd";
        }

        if (d.Length >= 6 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F')
        {
            return "gif";
        }

        if (d.Length >= 2 && d[0] == (byte)'#' && d[1] == (byte)'?')
        {
            return "hdr";
        }

        return null;
    }

    /// <summary>Box-filters an image down so neither side exceeds <paramref name="maxSize"/>.</summary>
    public static RgbaImage Downscale(RgbaImage image, int maxSize)
    {
        var factor = (int)Math.Ceiling(Math.Max(image.Width, image.Height) / (double)maxSize);
        if (factor <= 1)
        {
            return image;
        }

        var w = Math.Max(1, image.Width / factor);
        var h = Math.Max(1, image.Height / factor);
        var dst = new byte[w * h * 4];
        var src = image.Pixels;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                var n = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var sy = y * factor + dy;
                    if (sy >= image.Height)
                    {
                        break;
                    }

                    for (var dx = 0; dx < factor; dx++)
                    {
                        var sx = x * factor + dx;
                        if (sx >= image.Width)
                        {
                            break;
                        }

                        var i = 4 * (sy * image.Width + sx);
                        r += ColorSpace.SrgbByteToLinear(src[i]);
                        g += ColorSpace.SrgbByteToLinear(src[i + 1]);
                        b += ColorSpace.SrgbByteToLinear(src[i + 2]);
                        a += src[i + 3];
                        n++;
                    }
                }

                var o = 4 * (y * w + x);
                dst[o] = ColorSpace.LinearToSrgbByte(r / n);
                dst[o + 1] = ColorSpace.LinearToSrgbByte(g / n);
                dst[o + 2] = ColorSpace.LinearToSrgbByte(b / n);
                dst[o + 3] = (byte)Math.Round(a / n);
            }
        }

        return new RgbaImage(w, h, dst);
    }
}
