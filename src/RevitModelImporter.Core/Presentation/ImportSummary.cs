using System.Globalization;
using System.Text;

namespace RevitModelImporter.Core.Presentation;

/// <summary>What happened during an import, shown in the summary dialog.</summary>
public sealed class ImportSummary
{
    public string FileName { get; set; } = "";

    public bool Cancelled { get; set; }

    public int ElementsCreated { get; set; }

    public int MaterialsCreated { get; set; }

    public int MaterialsReused { get; set; }

    public long SourceTriangles { get; set; }

    public bool Simplified { get; set; }

    public long TrianglesAfterSimplify { get; set; }

    /// <summary>Triangles removed by cleanup (short edges, degenerate, duplicates) before reaching Revit.</summary>
    public long TrianglesRemovedByCleanup { get; set; }

    public long TrianglesSentToRevit { get; set; }

    /// <summary>Triangles Revit's TessellatedShapeBuilder reported as unusable (an estimate, see docs).</summary>
    public long TrianglesRejectedByRevit { get; set; }

    public int SolidsCreated { get; set; }

    public int MeshesCreated { get; set; }

    public List<string> MissingTextures { get; set; } = [];

    public string? TextureFolder { get; set; }

    public TimeSpan AnalysisTime { get; set; }

    public TimeSpan ConversionTime { get; set; }

    public TimeSpan CreationTime { get; set; }

    public TimeSpan TotalTime => AnalysisTime + ConversionTime + CreationTime;

    public List<string> Warnings { get; set; } = [];

    public int RevitWarningsSuppressed { get; set; }

    public IReadOnlyList<(string Label, string Value)> Lines()
    {
        var lines = new List<(string, string)>
        {
            ("Elements created", N(ElementsCreated) + (SolidsCreated + MeshesCreated > 0 ? $"  ({N(SolidsCreated)} solid/surface parts, {N(MeshesCreated)} mesh parts)" : "")),
            ("Materials", $"{N(MaterialsCreated)} created, {N(MaterialsReused)} reused"),
            ("Faces in the file", N(SourceTriangles)),
            ("Faces after simplifying", Simplified ? $"{N(TrianglesAfterSimplify)}  ({Ratio(TrianglesAfterSimplify, SourceTriangles)} of the original)" : "not simplified"),
            ("Faces removed for Revit tolerances", N(TrianglesRemovedByCleanup)),
            ("Faces sent to Revit", N(TrianglesSentToRevit)),
            ("Faces Revit rejected (estimate)", N(TrianglesRejectedByRevit) + (TrianglesSentToRevit > 0 && TrianglesRejectedByRevit > 0 ? $"  ({Ratio(TrianglesRejectedByRevit, TrianglesSentToRevit)})" : "")),
            ("Missing textures", MissingTextures.Count == 0 ? "none" : string.Join(", ", MissingTextures)),
            ("Elapsed", $"{Seconds(TotalTime)}  (analysis {Seconds(AnalysisTime)}, conversion {Seconds(ConversionTime)}, creating elements {Seconds(CreationTime)})"),
        };
        if (TextureFolder is not null)
        {
            lines.Add(("Texture folder", TextureFolder));
        }

        if (RevitWarningsSuppressed > 0)
        {
            lines.Add(("Revit warnings suppressed", N(RevitWarningsSuppressed)));
        }

        return lines;
    }

    public string Headline => Cancelled
        ? "Import cancelled. Nothing was added to the project."
        : $"Imported {FileName}: {N(ElementsCreated)} element(s), {N(TrianglesSentToRevit - TrianglesRejectedByRevit)} faces.";

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Headline);
        foreach (var (label, value) in Lines())
        {
            sb.Append(label).Append(": ").AppendLine(value);
        }

        if (Warnings.Count > 0)
        {
            sb.AppendLine("Warnings:");
            foreach (var w in Warnings)
            {
                sb.Append(" - ").AppendLine(w);
            }
        }

        if (TextureFolder is not null)
        {
            sb.AppendLine($"Textures live in {TextureFolder}. Revit stores only the path: keep this folder with the project.");
        }

        return sb.ToString();
    }

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Ratio(long a, long b) => b == 0 ? "0%" : ((double)a / b).ToString("P1", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
