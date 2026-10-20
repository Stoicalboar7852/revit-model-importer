using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitModelImporter.Revit.Infrastructure;

/// <summary>Draws the ribbon icons in code so the repository needs no binary image files.</summary>
internal static class IconFactory
{
    public static BitmapSource Import(int size) => Render(size, DrawImport);

    public static BitmapSource Settings(int size) => Render(size, DrawSettings);

    private static BitmapSource Render(int size, Action<DrawingContext, double> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            draw(dc, size);
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static void DrawImport(DrawingContext dc, double s)
    {
        // Isometric cube with an arrow pointing into it.
        var top = Brush(0x5B, 0x9B, 0xD5);
        var left = Brush(0x2E, 0x75, 0xB6);
        var right = Brush(0x1F, 0x4E, 0x79);
        var outline = new Pen(Brush(0x10, 0x2A, 0x43), Math.Max(1, s / 32));
        Point P(double x, double y) => new(x * s, y * s);
        dc.DrawGeometry(top, outline, Poly(P(0.50, 0.30), P(0.88, 0.48), P(0.50, 0.66), P(0.12, 0.48)));
        dc.DrawGeometry(left, outline, Poly(P(0.12, 0.48), P(0.50, 0.66), P(0.50, 0.98), P(0.12, 0.80)));
        dc.DrawGeometry(right, outline, Poly(P(0.50, 0.66), P(0.88, 0.48), P(0.88, 0.80), P(0.50, 0.98)));
        var arrow = Brush(0x37, 0xA1, 0x3C);
        dc.DrawGeometry(arrow, new Pen(Brush(0x1E, 0x5E, 0x22), Math.Max(1, s / 32)),
            Poly(P(0.42, 0.02), P(0.58, 0.02), P(0.58, 0.20), P(0.70, 0.20), P(0.50, 0.42), P(0.30, 0.20), P(0.42, 0.20)));
    }

    private static void DrawSettings(DrawingContext dc, double s)
    {
        var fill = Brush(0x60, 0x60, 0x60);
        var pen = new Pen(Brush(0x30, 0x30, 0x30), Math.Max(1, s / 32));
        var c = new Point(s / 2, s / 2);
        var teeth = new StreamGeometry();
        using (var g = teeth.Open())
        {
            const int n = 8;
            for (var i = 0; i < n * 2; i++)
            {
                var a = Math.PI * i / n;
                var r = (i % 2 == 0 ? 0.46 : 0.34) * s;
                var p = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
                if (i == 0)
                {
                    g.BeginFigure(p, true, true);
                }
                else
                {
                    g.LineTo(p, true, false);
                }
            }
        }

        teeth.Freeze();
        dc.DrawGeometry(fill, pen, teeth);
        dc.DrawEllipse(Brushes.White, pen, c, s * 0.14, s * 0.14);
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static StreamGeometry Poly(params Point[] points)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(points[0], true, true);
            g.PolyLineTo(points.Skip(1).ToList(), true, false);
        }

        geo.Freeze();
        return geo;
    }
}
