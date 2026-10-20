using Pfim;
using RevitModelImporter.Core.Scene;
using RevitModelImporter.Core.Textures;
using StbImageSharp;

namespace RevitModelImporter.Worker;

/// <summary>Decodes textures with permissively licensed managed decoders: StbImageSharp (PNG, JPEG, BMP, TGA,
/// PSD, GIF, HDR, PIC, PNM) and Pfim (DDS, TGA). WebP and KTX2 are not supported.</summary>
public sealed class ImageDecoder : ITextureDecoder
{
    public RgbaImage? TryDecode(byte[] data, string? nameHint, out string? error)
    {
        error = null;
        var kind = ImageFormats.Sniff(data);
        var ext = Path.GetExtension(nameHint ?? "").TrimStart('.').ToLowerInvariant();
        if (kind is "webp" or "ktx2" || ext is "webp" or "ktx2" or "ktx" or "basis")
        {
            error = $"{(kind ?? ext).ToUpperInvariant()} images are not supported.";
            return null;
        }

        if (kind == "dds" || ext == "dds")
        {
            return DecodeWithPfim(data, out error);
        }

        try
        {
            var img = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
            return new RgbaImage(img.Width, img.Height, img.Data);
        }
        catch (Exception e)
        {
            // StbImageSharp misses some TGA variants that Pfim handles.
            if (ext == "tga")
            {
                return DecodeWithPfim(data, out error);
            }

            error = e.Message;
            return null;
        }
    }

    private static RgbaImage? DecodeWithPfim(byte[] data, out string? error)
    {
        error = null;
        try
        {
            using var image = Pfimage.FromStream(new MemoryStream(data));
            if (image.Compressed)
            {
                image.Decompress();
            }

            var w = image.Width;
            var h = image.Height;
            var dst = new byte[w * h * 4];
            var src = image.Data;
            var stride = image.Stride;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var o = 4 * (y * w + x);
                    switch (image.Format)
                    {
                        case ImageFormat.Rgba32:
                        {
                            var i = y * stride + 4 * x;
                            dst[o] = src[i + 2];
                            dst[o + 1] = src[i + 1];
                            dst[o + 2] = src[i];
                            dst[o + 3] = src[i + 3];
                            break;
                        }

                        case ImageFormat.Rgb24:
                        {
                            var i = y * stride + 3 * x;
                            dst[o] = src[i + 2];
                            dst[o + 1] = src[i + 1];
                            dst[o + 2] = src[i];
                            dst[o + 3] = 255;
                            break;
                        }

                        case ImageFormat.Rgb8:
                        {
                            var v = src[y * stride + x];
                            dst[o] = dst[o + 1] = dst[o + 2] = v;
                            dst[o + 3] = 255;
                            break;
                        }

                        default:
                            error = $"Unsupported DDS/TGA pixel format {image.Format}.";
                            return null;
                    }
                }
            }

            return new RgbaImage(w, h, dst);
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }
}
