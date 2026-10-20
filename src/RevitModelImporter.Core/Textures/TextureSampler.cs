using RevitModelImporter.Core.Scene;

namespace RevitModelImporter.Core.Textures;

/// <summary>Bilinear, wrapping texture lookups returning linear-space colour.</summary>
public sealed class TextureSampler
{
    private readonly RgbaImage _image;
    private readonly float[] _linear; // RGBA linear, premultiplied lookups avoided for simplicity

    public TextureSampler(RgbaImage image)
    {
        _image = image;
        var px = image.Pixels;
        _linear = new float[image.Width * image.Height * 4];
        for (var i = 0; i < _linear.Length; i += 4)
        {
            _linear[i] = (float)ColorSpace.SrgbByteToLinear(px[i]);
            _linear[i + 1] = (float)ColorSpace.SrgbByteToLinear(px[i + 1]);
            _linear[i + 2] = (float)ColorSpace.SrgbByteToLinear(px[i + 2]);
            _linear[i + 3] = px[i + 3] / 255f;
        }
    }

    public int Width => _image.Width;

    public int Height => _image.Height;

    /// <summary>Samples at (u, v) with v = 0 at the bottom of the image (OpenGL/glTF-after-Assimp convention).</summary>
    public ColorRgba Sample(double u, double v)
    {
        if (double.IsNaN(u) || double.IsNaN(v))
        {
            return ColorRgba.White;
        }

        var w = _image.Width;
        var h = _image.Height;
        var x = (u - Math.Floor(u)) * w - 0.5;
        var y = (1.0 - (v - Math.Floor(v))) * h - 0.5;
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var c00 = Texel(x0, y0);
        var c10 = Texel(x0 + 1, y0);
        var c01 = Texel(x0, y0 + 1);
        var c11 = Texel(x0 + 1, y0 + 1);
        return new ColorRgba(
            Lerp(Lerp(c00.R, c10.R, fx), Lerp(c01.R, c11.R, fx), fy),
            Lerp(Lerp(c00.G, c10.G, fx), Lerp(c01.G, c11.G, fx), fy),
            Lerp(Lerp(c00.B, c10.B, fx), Lerp(c01.B, c11.B, fx), fy),
            Lerp(Lerp(c00.A, c10.A, fx), Lerp(c01.A, c11.A, fx), fy));
    }

    /// <summary>Mean linear colour of the whole image.</summary>
    public ColorRgba Average()
    {
        double r = 0, g = 0, b = 0, a = 0;
        var n = _linear.Length / 4;
        for (var i = 0; i < _linear.Length; i += 4)
        {
            r += _linear[i];
            g += _linear[i + 1];
            b += _linear[i + 2];
            a += _linear[i + 3];
        }

        return new ColorRgba(r / n, g / n, b / n, a / n);
    }

    private ColorRgba Texel(int x, int y)
    {
        x = Mod(x, _image.Width);
        y = Mod(y, _image.Height);
        var i = 4 * (y * _image.Width + x);
        return new ColorRgba(_linear[i], _linear[i + 1], _linear[i + 2], _linear[i + 3]);
    }

    private static int Mod(int a, int m)
    {
        var r = a % m;
        return r < 0 ? r + m : r;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
