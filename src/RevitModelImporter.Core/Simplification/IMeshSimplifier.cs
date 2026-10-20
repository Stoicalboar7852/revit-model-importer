using System.Text.Json.Serialization;
using RevitModelImporter.Core.Processing;

namespace RevitModelImporter.Core.Simplification;

/// <summary>Reduces the triangle count of a mesh in place.</summary>
public interface IMeshSimplifier
{
    /// <summary>Simplifies <paramref name="mesh"/> to about <paramref name="targetTriangles"/> triangles.
    /// Open borders and material seams are preserved, per-triangle materials are kept and per-corner UVs
    /// are carried through each collapse.</summary>
    SimplifyResult Simplify(ObjectMesh mesh, int targetTriangles, CancellationToken cancellationToken = default);
}

public sealed record SimplifyResult(int InputTriangles, int OutputTriangles, int Iterations, bool TargetReached);

[JsonConverter(typeof(JsonStringEnumConverter<SimplifyTargetKind>))]
public enum SimplifyTargetKind
{
    /// <summary>Keep this fraction of the triangles (0 &lt; value ≤ 1).</summary>
    Ratio,

    /// <summary>Keep about this many triangles in total.</summary>
    TriangleCount,
}

/// <summary>A simplification target for a whole model.</summary>
public readonly record struct SimplifyTarget(SimplifyTargetKind Kind, double Value)
{
    public static SimplifyTarget Ratio(double ratio) => new(SimplifyTargetKind.Ratio, ratio);

    public static SimplifyTarget Count(long triangles) => new(SimplifyTargetKind.TriangleCount, triangles);

    /// <summary>Total triangle target for a model with <paramref name="totalTriangles"/> triangles.</summary>
    public long ResolveTotal(long totalTriangles)
    {
        var target = Kind == SimplifyTargetKind.Ratio
            ? (long)Math.Round(totalTriangles * Math.Clamp(Value, 0.0, 1.0))
            : (long)Math.Round(Value);
        return Math.Clamp(target, 0, totalTriangles);
    }

    /// <summary>Parses "0.25", "25%" (ratios) or "50000" (count) as used on the worker command line.</summary>
    public static bool TryParse(string? text, out SimplifyTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (t.EndsWith('%'))
        {
            if (double.TryParse(t[..^1], System.Globalization.NumberStyles.Float, inv, out var pct) && pct > 0 && pct <= 100)
            {
                target = Ratio(pct / 100.0);
                return true;
            }

            return false;
        }

        if (double.TryParse(t, System.Globalization.NumberStyles.Float, inv, out var v) && v > 0)
        {
            if (v <= 1.0 && t.Contains('.', StringComparison.Ordinal))
            {
                target = Ratio(v);
                return true;
            }

            if (v >= 1 && Math.Abs(v - Math.Round(v)) < 1e-9)
            {
                target = Count((long)v);
                return true;
            }
        }

        return false;
    }

    public string ToArgument() => Kind == SimplifyTargetKind.Ratio
        ? Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
        : ((long)Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
