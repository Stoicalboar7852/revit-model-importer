using System.Text.Json.Serialization;
using RevitModelImporter.Core.Simplification;
using RevitModelImporter.Core.Statistics;
using RevitModelImporter.Core.Units;

namespace RevitModelImporter.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<PlacementMode>))]
public enum PlacementMode
{
    /// <summary>Model coordinates are used as Revit internal coordinates.</summary>
    InternalOrigin,

    /// <summary>The model origin is placed at the project base point.</summary>
    ProjectBasePoint,

    /// <summary>The model is centred on the internal origin in plan with its base at elevation 0.</summary>
    CenterOnOrigin,
}

[JsonConverter(typeof(JsonStringEnumConverter<GroupingMode>))]
public enum GroupingMode
{
    /// <summary>One DirectShape per object (scene node) in the file.</summary>
    PerObject,

    /// <summary>Everything in one DirectShape.</summary>
    SingleElement,
}

[JsonConverter(typeof(JsonStringEnumConverter<GeometryMode>))]
public enum GeometryMode
{
    /// <summary>TessellatedShapeBuilder target AnyGeometry with Mesh fallback: solids/surfaces where possible.</summary>
    SolidsWithMeshFallback,

    /// <summary>TessellatedShapeBuilder target Mesh: faster, never solids.</summary>
    MeshOnly,
}

/// <summary>Per-user settings, stored as JSON in %AppData%\RevitModelImporter\settings.json.</summary>
public sealed class ImporterSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Show the inline "simplify" recommendation in the Import dialog.</summary>
    public bool ShowSimplifyRecommendation { get; set; } = true;

    /// <summary>Recommend simplifying above this many triangles.</summary>
    public long RecommendThreshold { get; set; } = SimplificationAdvisor.DefaultThreshold;

    /// <summary>Recommend it more strongly above this many triangles.</summary>
    public long StrongRecommendThreshold { get; set; } = SimplificationAdvisor.DefaultStrongThreshold;

    /// <summary>Texture folder used when the project has not been saved yet. Null → %LocalAppData%\RevitModelImporter\textures.</summary>
    public string? FallbackTextureFolder { get; set; }

    /// <summary>Always use <see cref="FallbackTextureFolder"/>, even for saved projects.</summary>
    public bool AlwaysUseFallbackTextureFolder { get; set; }

    /// <summary>Folders chosen with "Locate textures folder…", most recent first.</summary>
    public List<string> TextureSearchFolders { get; set; } = [];

    public string? LastModelFolder { get; set; }

    /// <summary>Triangles per TessellatedShapeBuilder.Build call; large material groups are split into chunks.</summary>
    public int MaxTrianglesPerBuild { get; set; } = 20_000;

    /// <summary>Warn about wrong units when more than this fraction of triangles would be lost to short edges.</summary>
    public double ShortEdgeWarningFraction { get; set; } = 0.05;

    public LastUsedOptions LastUsed { get; set; } = new();

    /// <summary>Clamps values a hand-edited file might have broken.</summary>
    public ImporterSettings Normalize()
    {
        RecommendThreshold = Math.Max(1, RecommendThreshold);
        StrongRecommendThreshold = Math.Max(RecommendThreshold, StrongRecommendThreshold);
        MaxTrianglesPerBuild = Math.Clamp(MaxTrianglesPerBuild, 100, 1_000_000);
        ShortEdgeWarningFraction = Math.Clamp(ShortEdgeWarningFraction, 0.0, 1.0);
        TextureSearchFolders ??= [];
        TextureSearchFolders = TextureSearchFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        LastUsed ??= new LastUsedOptions();
        LastUsed.Normalize();
        return this;
    }
}

public sealed class LastUsedOptions
{
    /// <summary>Unit last chosen for formats that do not state their unit, keyed by extension.</summary>
    public Dictionary<string, LengthUnit> UnitByFormat { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Up axis last chosen, keyed by extension.</summary>
    public Dictionary<string, UpAxis> UpAxisByFormat { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PlacementMode Placement { get; set; } = PlacementMode.InternalOrigin;

    /// <summary>BuiltInCategory name, e.g. "OST_GenericModel".</summary>
    public string Category { get; set; } = "OST_GenericModel";

    public GroupingMode Grouping { get; set; } = GroupingMode.PerObject;

    /// <summary>Put the imported elements in one Revit model group named after the file, so the import is listed in
    /// the Project Browser (Groups ▸ Model) and selects and moves as one. DirectShapes alone are not listed there.</summary>
    public bool CreateGroup { get; set; } = true;

    public GeometryMode GeometryMode { get; set; } = GeometryMode.SolidsWithMeshFallback;

    /// <summary>Simplify is never switched on by the importer; this is only the user's own last choice.</summary>
    public bool SimplifyEnabled { get; set; }

    public SimplifyTargetKind SimplifyKind { get; set; } = SimplifyTargetKind.Ratio;

    /// <summary>Percentage of faces to keep, 1–100.</summary>
    public double SimplifyPercent { get; set; } = 25;

    public long SimplifyTriangleCount { get; set; } = 45_000;

    public int PaletteSize { get; set; } = 32;

    public void Normalize()
    {
        UnitByFormat = new Dictionary<string, LengthUnit>(UnitByFormat ?? [], StringComparer.OrdinalIgnoreCase);
        UpAxisByFormat = new Dictionary<string, UpAxis>(UpAxisByFormat ?? [], StringComparer.OrdinalIgnoreCase);
        Category = string.IsNullOrWhiteSpace(Category) ? "OST_GenericModel" : Category;
        SimplifyPercent = Math.Clamp(SimplifyPercent, 0.1, 100);
        SimplifyTriangleCount = Math.Max(4, SimplifyTriangleCount);
        PaletteSize = Math.Clamp(PaletteSize, 2, 256);
    }
}
