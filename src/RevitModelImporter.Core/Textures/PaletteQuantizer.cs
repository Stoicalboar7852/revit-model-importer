using RevitModelImporter.Core.Scene;

namespace RevitModelImporter.Core.Textures;

/// <summary>Reduces many weighted colours to a small palette with weighted k-means (k-means++ seeding)
/// in the OKLab perceptual space. Deterministic for the same input.</summary>
public static class PaletteQuantizer
{
    public sealed record Result(IReadOnlyList<ColorRgba> Palette, int[] Assignment);

    /// <param name="colors">Linear colours (alpha is averaged per cluster, not used for distance).</param>
    /// <param name="weights">Weight per colour, e.g. triangle area.</param>
    public static Result Quantize(IReadOnlyList<ColorRgba> colors, IReadOnlyList<double> weights, int paletteSize, int iterations = 12)
    {
        var n = colors.Count;
        if (n == 0)
        {
            return new Result([], []);
        }

        paletteSize = Math.Max(1, paletteSize);

        // Bin identical-looking colours (6 bits per channel) so k-means runs on at most 2^18 points.
        var binOf = new int[n];
        var binIndex = new Dictionary<int, int>();
        var bins = new List<Bin>();
        for (var i = 0; i < n; i++)
        {
            var (r, g, b) = colors[i].ToSrgbBytes();
            var key = (r >> 2) << 12 | (g >> 2) << 6 | (b >> 2);
            if (!binIndex.TryGetValue(key, out var bi))
            {
                bi = bins.Count;
                binIndex.Add(key, bi);
                bins.Add(new Bin());
            }

            var w = Math.Max(weights[i], 1e-12);
            var lab = OkLab.FromLinear(colors[i]);
            var bin = bins[bi];
            bin.Weight += w;
            bin.L += lab.L * w;
            bin.A += lab.A * w;
            bin.B += lab.B * w;
            bin.Alpha += colors[i].A * w;
            binOf[i] = bi;
        }

        foreach (var bin in bins)
        {
            bin.L /= bin.Weight;
            bin.A /= bin.Weight;
            bin.B /= bin.Weight;
            bin.Alpha /= bin.Weight;
        }

        var k = Math.Min(paletteSize, bins.Count);
        var centers = SeedPlusPlus(bins, k);
        var assign = new int[bins.Count];
        for (var it = 0; it < iterations; it++)
        {
            var changed = false;
            for (var i = 0; i < bins.Count; i++)
            {
                var best = Nearest(centers, bins[i]);
                if (best != assign[i] || it == 0)
                {
                    changed |= best != assign[i];
                    assign[i] = best;
                }
            }

            var sums = new double[k, 5];
            for (var i = 0; i < bins.Count; i++)
            {
                var c = assign[i];
                var w = bins[i].Weight;
                sums[c, 0] += w;
                sums[c, 1] += bins[i].L * w;
                sums[c, 2] += bins[i].A * w;
                sums[c, 3] += bins[i].B * w;
                sums[c, 4] += bins[i].Alpha * w;
            }

            for (var c = 0; c < k; c++)
            {
                if (sums[c, 0] > 0)
                {
                    centers[c] = new Center(sums[c, 1] / sums[c, 0], sums[c, 2] / sums[c, 0], sums[c, 3] / sums[c, 0], sums[c, 4] / sums[c, 0]);
                }
            }

            if (!changed && it > 0)
            {
                break;
            }
        }

        // Drop empty clusters and renumber.
        var used = new int[k];
        Array.Fill(used, -1);
        var palette = new List<ColorRgba>();
        for (var i = 0; i < bins.Count; i++)
        {
            var c = assign[i];
            if (used[c] < 0)
            {
                used[c] = palette.Count;
                var lin = OkLab.ToLinear(centers[c].L, centers[c].A, centers[c].B);
                palette.Add(lin with { A = Math.Clamp(centers[c].Alpha, 0, 1) });
            }
        }

        var assignment = new int[n];
        for (var i = 0; i < n; i++)
        {
            assignment[i] = used[assign[binOf[i]]];
        }

        return new Result(palette, assignment);
    }

    private static Center[] SeedPlusPlus(List<Bin> bins, int k)
    {
        var rng = new Random(12345);
        var centers = new Center[k];
        // First centre: the heaviest bin (deterministic and usually the dominant colour).
        var first = 0;
        for (var i = 1; i < bins.Count; i++)
        {
            if (bins[i].Weight > bins[first].Weight)
            {
                first = i;
            }
        }

        centers[0] = new Center(bins[first].L, bins[first].A, bins[first].B, bins[first].Alpha);
        var dist = new double[bins.Count];
        for (var i = 0; i < bins.Count; i++)
        {
            dist[i] = Dist(centers[0], bins[i]);
        }

        for (var c = 1; c < k; c++)
        {
            double total = 0;
            for (var i = 0; i < bins.Count; i++)
            {
                total += dist[i] * bins[i].Weight;
            }

            var pick = 0;
            if (total > 0)
            {
                var r = rng.NextDouble() * total;
                for (var i = 0; i < bins.Count; i++)
                {
                    r -= dist[i] * bins[i].Weight;
                    if (r <= 0)
                    {
                        pick = i;
                        break;
                    }
                }
            }
            else
            {
                pick = c % bins.Count;
            }

            centers[c] = new Center(bins[pick].L, bins[pick].A, bins[pick].B, bins[pick].Alpha);
            for (var i = 0; i < bins.Count; i++)
            {
                dist[i] = Math.Min(dist[i], Dist(centers[c], bins[i]));
            }
        }

        return centers;
    }

    private static int Nearest(Center[] centers, Bin b)
    {
        var best = 0;
        var bestD = double.MaxValue;
        for (var c = 0; c < centers.Length; c++)
        {
            var d = Dist(centers[c], b);
            if (d < bestD)
            {
                bestD = d;
                best = c;
            }
        }

        return best;
    }

    private static double Dist(Center c, Bin b)
    {
        var dl = c.L - b.L;
        var da = c.A - b.A;
        var db = c.B - b.B;
        return dl * dl + da * da + db * db;
    }

    private sealed class Bin
    {
        public double Weight;
        public double L;
        public double A;
        public double B;
        public double Alpha;
    }

    private readonly record struct Center(double L, double A, double B, double Alpha);
}

/// <summary>Björn Ottosson's OKLab colour space (public domain formulas).</summary>
public static class OkLab
{
    public static (double L, double A, double B) FromLinear(ColorRgba c)
    {
        var l = Math.Cbrt(0.4122214708 * c.R + 0.5363325363 * c.G + 0.0514459929 * c.B);
        var m = Math.Cbrt(0.2119034982 * c.R + 0.6806995451 * c.G + 0.1073969566 * c.B);
        var s = Math.Cbrt(0.0883024619 * c.R + 0.2817188376 * c.G + 0.6299787005 * c.B);
        return (
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    public static ColorRgba ToLinear(double lightness, double a, double b)
    {
        var l = lightness + 0.3963377774 * a + 0.2158037573 * b;
        var m = lightness - 0.1055613458 * a - 0.0638541728 * b;
        var s = lightness - 0.0894841775 * a - 1.2914855480 * b;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        return new ColorRgba(
            Math.Clamp(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, 0, 1),
            Math.Clamp(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s, 0, 1),
            Math.Clamp(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s, 0, 1));
    }
}
