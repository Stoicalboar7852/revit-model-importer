using RevitModelImporter.Core.Processing;

namespace RevitModelImporter.Core.Statistics;

/// <summary>Histogram of each triangle's shortest edge, in source units, on a log2 scale with
/// <see cref="BinsPerOctave"/> bins per doubling. Lets the dialog estimate, for any unit the user picks,
/// how many triangles Revit's short-curve tolerance would remove — before importing.</summary>
public sealed class EdgeLengthHistogram
{
    public const int BinsPerOctave = 4;
    public const int MinExponent = -40;
    public const int MaxExponent = 40;
    public const int BinCount = (MaxExponent - MinExponent) * BinsPerOctave;

    /// <summary>Triangle counts per bin. Bin i covers [2^(MinExponent + i/BinsPerOctave), 2^(MinExponent + (i+1)/BinsPerOctave)).</summary>
    public long[] Counts { get; set; } = new long[BinCount];

    /// <summary>Triangles whose shortest edge has zero length.</summary>
    public long ZeroLength { get; set; }

    public long Total { get; set; }

    public void Add(double shortestEdge)
    {
        Total++;
        if (!(shortestEdge > 0))
        {
            ZeroLength++;
            return;
        }

        var bin = (int)Math.Floor((Math.Log2(shortestEdge) - MinExponent) * BinsPerOctave);
        Counts[Math.Clamp(bin, 0, BinCount - 1)]++;
    }

    public void AddMesh(ObjectMesh mesh)
    {
        var p = mesh.Positions;
        var idx = mesh.Indices;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = idx[3 * t];
            var b = idx[3 * t + 1];
            var c = idx[3 * t + 2];
            var ab = DistSq(p, a, b);
            var bc = DistSq(p, b, c);
            var ca = DistSq(p, c, a);
            Add(Math.Sqrt(Math.Min(ab, Math.Min(bc, ca))));
        }
    }

    /// <summary>Estimated number of triangles whose shortest edge is below <paramref name="threshold"/>
    /// (same units as the histogram). Interpolates linearly in log space inside the partial bin.</summary>
    public double EstimateCountBelow(double threshold)
    {
        if (!(threshold > 0))
        {
            return 0;
        }

        var pos = (Math.Log2(threshold) - MinExponent) * BinsPerOctave;
        double sum = ZeroLength;
        if (pos <= 0)
        {
            return sum;
        }

        var full = (int)Math.Floor(pos);
        for (var i = 0; i < Math.Min(full, BinCount); i++)
        {
            sum += Counts[i];
        }

        if (full < BinCount)
        {
            sum += Counts[full] * (pos - full);
        }

        return sum;
    }

    public double EstimateFractionBelow(double threshold) => Total == 0 ? 0 : EstimateCountBelow(threshold) / Total;

    private static double DistSq(double[] p, int a, int b)
    {
        var dx = p[3 * a] - p[3 * b];
        var dy = p[3 * a + 1] - p[3 * b + 1];
        var dz = p[3 * a + 2] - p[3 * b + 2];
        return dx * dx + dy * dy + dz * dz;
    }
}
