using System.Text.Json;
using System.Text.Json.Serialization;
using RevitModelImporter.Core.Intermediate;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Statistics;
using RevitModelImporter.Core.Units;

namespace RevitModelImporter.Core.WorkerProtocol;

/// <summary>One line of worker stdout. Exactly one of the payload properties is set, depending on <see cref="Type"/>.</summary>
public sealed class WorkerMessage
{
    public const string ProgressType = "progress";
    public const string WarningType = "warning";
    public const string ResultType = "result";
    public const string ErrorType = "error";

    public string Type { get; set; } = "";

    public string? Stage { get; set; }

    /// <summary>0..1 overall progress, if known.</summary>
    public double? Fraction { get; set; }

    public string? Message { get; set; }

    /// <summary>Exit code the worker is about to return, for error messages.</summary>
    public int? Code { get; set; }

    public FormatsResult? Formats { get; set; }

    public AnalysisResult? Analysis { get; set; }

    public ConversionResult? Conversion { get; set; }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // Output is read by our own client and by people in a console, never embedded in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJsonLine() => JsonSerializer.Serialize(this, JsonOptions);

    public static WorkerMessage? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkerMessage>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class FormatsResult
{
    public string AssimpVersion { get; set; } = "";

    /// <summary>.NET runtime the worker is running on (diagnostics: it targets net8.0 and rolls forward).</summary>
    public string RuntimeVersion { get; set; } = "";

    /// <summary>Importable formats (Revit-native and animation-only formats removed).</summary>
    public List<FormatInfo> Formats { get; set; } = [];
}

public sealed class FormatInfo
{
    /// <summary>Lower-case extension without the dot.</summary>
    public string Extension { get; set; } = "";

    public string Description { get; set; } = "";
}

/// <summary>What the worker found in a file; drives the Import dialog.</summary>
public sealed class AnalysisResult
{
    public string FilePath { get; set; } = "";

    public string FormatId { get; set; } = "";

    public long FileSizeBytes { get; set; }

    public int ObjectCount { get; set; }

    public int MeshCount { get; set; }

    /// <summary>Faces after triangulation: a quad counts as 2, an n-gon as n − 2.</summary>
    public long TriangleCount { get; set; }

    /// <summary>Polygons as stored in the file, before triangulation (0 if unknown).</summary>
    public long SourcePolygonCount { get; set; }

    public long VertexCount { get; set; }

    /// <summary>Materials used by at least one triangle.</summary>
    public int MaterialCount { get; set; }

    public int TextureCount { get; set; }

    public int TexturesFound { get; set; }

    public int TexturesEmbedded { get; set; }

    public List<string> MissingTextures { get; set; } = [];

    /// <summary>Triangle counts of the objects that will be created (for live simplify estimates).</summary>
    public List<int> ObjectTriangleCounts { get; set; } = [];

    /// <summary>Bounds in source units and source axes, after node transforms.</summary>
    public double[] BoundsMin { get; set; } = [0, 0, 0];

    public double[] BoundsMax { get; set; } = [0, 0, 0];

    public LengthUnit? DetectedUnit { get; set; }

    public string? UnitSource { get; set; }

    public UpAxis DefaultUpAxis { get; set; }

    public string? UpAxisSource { get; set; }

    public bool HasTexCoords { get; set; }

    public bool HasTextures { get; set; }

    public double UvOutsideUnitFraction { get; set; }

    public MaterialMode RecommendedMaterialMode { get; set; }

    public string? MaterialModeReason { get; set; }

    /// <summary>Shortest-edge histogram in source units.</summary>
    public EdgeLengthHistogram ShortestEdges { get; set; } = new();

    public List<string> Warnings { get; set; } = [];

    [JsonIgnore]
    public Geometry.Vec3 SourceSize => new(BoundsMax[0] - BoundsMin[0], BoundsMax[1] - BoundsMin[1], BoundsMax[2] - BoundsMin[2]);
}

public sealed class ConversionResult
{
    public string OutputPath { get; set; } = "";

    public ConversionStats Stats { get; set; } = new();

    public List<string> Warnings { get; set; } = [];
}
