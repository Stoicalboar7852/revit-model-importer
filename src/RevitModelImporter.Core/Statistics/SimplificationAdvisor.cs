namespace RevitModelImporter.Core.Statistics;

public enum RecommendationLevel
{
    None,
    Recommended,
    StronglyRecommended,
}

/// <summary>Result of evaluating a face count against the user's thresholds.</summary>
public sealed record SimplificationRecommendation(
    RecommendationLevel Level,
    long FaceCount,
    long Threshold,
    long StrongThreshold,
    long SuggestedTriangles,
    double SuggestedRatio)
{
    public bool IsVisible => Level != RecommendationLevel.None;
}

/// <summary>Pure decision logic for the "recommend simplifying, never force it" rule.
/// The view model shows the result inline; nothing here ever changes the user's options.</summary>
public static class SimplificationAdvisor
{
    public const long DefaultThreshold = 50_000;
    public const long DefaultStrongThreshold = 250_000;

    /// <summary>The suggestion aims a little under the threshold so small differences in the estimate do not
    /// leave the result just above it.</summary>
    public const double TargetHeadroom = 0.9;

    public static SimplificationRecommendation Evaluate(long faceCount, long threshold, long strongThreshold, bool enabled = true)
    {
        threshold = Math.Max(1, threshold);
        strongThreshold = Math.Max(threshold, strongThreshold);
        var level = !enabled || faceCount <= threshold
            ? RecommendationLevel.None
            : faceCount > strongThreshold ? RecommendationLevel.StronglyRecommended : RecommendationLevel.Recommended;

        var suggested = SuggestTarget(faceCount, threshold);
        var ratio = faceCount > 0 ? (double)suggested / faceCount : 1.0;
        return new SimplificationRecommendation(level, faceCount, threshold, strongThreshold, suggested, ratio);
    }

    /// <summary>A triangle target that brings the model under the threshold, rounded to a readable number.</summary>
    public static long SuggestTarget(long faceCount, long threshold)
    {
        if (faceCount <= threshold)
        {
            return faceCount;
        }

        var target = (long)Math.Floor(threshold * TargetHeadroom);
        target = RoundDown(target);
        return Math.Clamp(target, 1, faceCount);
    }

    private static long RoundDown(long value)
    {
        if (value < 1_000)
        {
            return value;
        }

        // Two significant digits: 45,000 stays, 225,000 stays, 46,789 → 46,000.
        var magnitude = (long)Math.Pow(10, Math.Floor(Math.Log10(value)) - 1);
        return value / magnitude * magnitude;
    }

    public static string FormatCount(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static string Title(SimplificationRecommendation r) => r.Level switch
    {
        RecommendationLevel.StronglyRecommended => "Simplifying is strongly recommended",
        RecommendationLevel.Recommended => "Simplifying is recommended",
        _ => "",
    };

    /// <summary>Inline notice text: how many faces, why that hurts in Revit, and a concrete suggestion.</summary>
    public static string Message(SimplificationRecommendation r)
    {
        if (r.Level == RecommendationLevel.None)
        {
            return "";
        }

        var faces = FormatCount(r.FaceCount);
        var suggestion = $"Suggested: simplify to about {FormatCount(r.SuggestedTriangles)} faces ({r.SuggestedRatio:P0} of the original), which brings it under {FormatCount(r.Threshold)}.";
        return r.Level == RecommendationLevel.StronglyRecommended
            ? $"This model has {faces} faces (triangles), far above {FormatCount(r.StrongThreshold)}. At this size Revit views will be slow to draw, every regeneration touching it will lag, and the project file will grow considerably. {suggestion} You can still import it as it is."
            : $"This model has {faces} faces (triangles), more than {FormatCount(r.Threshold)}. Detailed meshes make Revit views slower to draw and regenerate and make the project file larger. {suggestion} You can still import it as it is.";
    }
}
