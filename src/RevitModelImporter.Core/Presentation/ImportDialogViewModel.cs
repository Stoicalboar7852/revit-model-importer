using System.Globalization;
using System.Windows.Input;
using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Settings;
using RevitModelImporter.Core.Simplification;
using RevitModelImporter.Core.Statistics;
using RevitModelImporter.Core.Units;
using RevitModelImporter.Core.WorkerProtocol;

namespace RevitModelImporter.Core.Presentation;

/// <summary>Host-specific inputs for the Import dialog (supplied by the Revit add-in, or by tests).</summary>
public sealed class ImportDialogContext
{
    public IReadOnlyList<CategoryOption> Categories { get; init; } = [];

    /// <summary>Revit's Application.ShortCurveTolerance in feet, used to estimate short-edge losses per unit.</summary>
    public double ShortCurveToleranceFeet { get; init; } = CleanupOptions.RevitDefaults.MinEdgeLength;

    /// <summary>Where textures will be copied, for display.</summary>
    public string TextureFolder { get; init; } = "";

    /// <summary>True when <see cref="TextureFolder"/> is next to the saved project.</summary>
    public bool TextureFolderIsNextToProject { get; init; }

    /// <summary>Opens a folder picker; returns the chosen folder or null.</summary>
    public Func<string?>? PickFolder { get; init; }

    /// <summary>Re-runs the analysis with extra texture search folders.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<AnalysisResult>>? Reanalyze { get; init; }
}

/// <summary>Everything the user chose in the Import dialog.</summary>
public sealed record ImportChoices(
    LengthUnit Unit,
    UpAxis UpAxis,
    PlacementMode Placement,
    CategoryOption? Category,
    GroupingMode Grouping,
    bool CreateGroup,
    GeometryMode GeometryMode,
    MaterialMode MaterialMode,
    int PaletteSize,
    SimplifyTarget? Simplify,
    IReadOnlyList<string> TextureSearchFolders);

/// <summary>
/// View model of the Import dialog. Holds the "recommend simplifying, never force it" rules:
/// <list type="bullet">
/// <item>The face count is the triangle count after triangulation.</item>
/// <item>Above the threshold an inline notice appears (never a pop-up) with the face count, why it hurts, a suggested
/// target and an "Apply recommendation" command; above the strong threshold the wording is stronger.</item>
/// <item>Simplify starts in the user's last-used state and is never switched on by the analysis.</item>
/// <item>Import is always enabled and never asks for confirmation.</item>
/// <item>Any model can be simplified; the notice can be turned off in settings.</item>
/// </list>
/// </summary>
public sealed class ImportDialogViewModel : ObservableObject
{
    private readonly ImporterSettings _settings;
    private readonly ImportDialogContext _context;
    private readonly List<string> _textureSearchFolders;
    private AnalysisResult _analysis;
    private Option<LengthUnit> _selectedUnit;
    private Option<UpAxis> _selectedUpAxis;
    private Option<PlacementMode> _selectedPlacement;
    private CategoryOption? _selectedCategory;
    private Option<GroupingMode> _selectedGrouping;
    private bool _createGroup;
    private Option<GeometryMode> _selectedGeometryMode;
    private Option<MaterialMode> _selectedMaterialMode;
    private int _paletteSize;
    private bool _isSimplifyEnabled;
    private SimplifyTargetKind _simplifyKind;
    private double _simplifyPercent;
    private long _simplifyTriangleCount;
    private bool _isBusy;
    private string? _statusMessage;

    public ImportDialogViewModel(AnalysisResult analysis, ImporterSettings settings, ImportDialogContext context)
    {
        _analysis = analysis;
        _settings = settings;
        _context = context;
        _textureSearchFolders = [.. settings.TextureSearchFolders];
        var last = settings.LastUsed;

        Units = LengthUnits.All.Select(u => new Option<LengthUnit>(u, u.DisplayName())).ToList();
        UpAxes = [new(UpAxis.YUp, "Y-up"), new(UpAxis.ZUp, "Z-up")];
        Placements =
        [
            new(PlacementMode.InternalOrigin, "Internal origin", "Model coordinates are used as they are."),
            new(PlacementMode.ProjectBasePoint, "Project base point", "The model origin goes to the project base point."),
            new(PlacementMode.CenterOnOrigin, "Centered on the origin", "Centered in plan on the internal origin, base at elevation 0."),
        ];
        Groupings =
        [
            new(GroupingMode.PerObject, "One element per object"),
            new(GroupingMode.SingleElement, "Single merged element"),
        ];
        GeometryModes =
        [
            new(GeometryMode.SolidsWithMeshFallback, "Solids where possible (mesh fallback)", "Closed objects become solids; everything else becomes a mesh."),
            new(GeometryMode.MeshOnly, "Mesh only (faster)", "Always a mesh: faster to create, cannot be cut or joined."),
        ];
        MaterialModeOptions = Enum.GetValues<MaterialMode>().Select(m => new Option<MaterialMode>(m, m.DisplayName(), m.Explanation())).ToList();
        Categories = context.Categories;

        // Units: what the file says, else the user's last choice for this format, else a per-format guess.
        var unit = analysis.DetectedUnit
            ?? (last.UnitByFormat.TryGetValue(analysis.FormatId, out var lu) ? lu : FormatDefaults.GuessUnit(analysis.FormatId));
        _selectedUnit = Units.First(u => u.Value == unit);
        var up = analysis.UpAxisSource is { } src && (src.Contains("glTF", StringComparison.Ordinal) || src.Contains("FBX", StringComparison.Ordinal) || src.Contains("Collada", StringComparison.Ordinal))
            ? analysis.DefaultUpAxis
            : last.UpAxisByFormat.TryGetValue(analysis.FormatId, out var lup) ? lup : analysis.DefaultUpAxis;
        _selectedUpAxis = UpAxes.First(a => a.Value == up);
        _selectedPlacement = Placements.First(p => p.Value == last.Placement);
        _selectedCategory = Categories.FirstOrDefault(c => c.BuiltInName == last.Category)
            ?? Categories.FirstOrDefault(c => c.BuiltInName == "OST_GenericModel")
            ?? (Categories.Count > 0 ? Categories[0] : null);
        _selectedGrouping = Groupings.First(g => g.Value == last.Grouping);
        _createGroup = last.CreateGroup;
        _selectedGeometryMode = GeometryModes.First(g => g.Value == last.GeometryMode);
        _selectedMaterialMode = MaterialModeOptions.First(m => m.Value == analysis.RecommendedMaterialMode);
        _paletteSize = last.PaletteSize;

        // Simplify starts exactly as the user left it last time (off by default). Never derived from the analysis.
        _isSimplifyEnabled = last.SimplifyEnabled;
        _simplifyKind = last.SimplifyKind;
        _simplifyPercent = last.SimplifyPercent;
        _simplifyTriangleCount = last.SimplifyTriangleCount;

        ApplyRecommendationCommand = new RelayCommand(ApplyRecommendation, () => IsRecommendationVisible);
        ImportCommand = new RelayCommand(() => Close(true));
        CancelCommand = new RelayCommand(() => Close(false));
        LocateTexturesCommand = new RelayCommand(async () => await LocateTexturesAsync().ConfigureAwait(true), () => context.PickFolder is not null && !IsBusy);
    }

    /// <summary>Raised when the view should close; the argument is the dialog result.</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool? DialogResult { get; private set; }

    // ----- Statistics -----------------------------------------------------------------------------

    public AnalysisResult Analysis => _analysis;

    public string FileName => Path.GetFileName(_analysis.FilePath);

    public string FormatName => _analysis.FormatId.ToUpperInvariant();

    public int ObjectCount => _analysis.ObjectCount;

    public int MeshCount => _analysis.MeshCount;

    /// <summary>Faces = triangles after triangulation (a quad counts as two).</summary>
    public long FaceCount => _analysis.TriangleCount;

    public long VertexCount => _analysis.VertexCount;

    public int MaterialCount => _analysis.MaterialCount;

    public string FaceCountText => $"{N(FaceCount)} faces (triangles)";

    /// <summary>Shown when the file stores polygons that were triangulated.</summary>
    public string? PolygonNote =>
        _analysis.SourcePolygonCount > 0 && _analysis.SourcePolygonCount != _analysis.TriangleCount
            ? $"from {N(_analysis.SourcePolygonCount)} polygons in the file (quads and n-gons count as several triangles)"
            : null;

    public string ObjectsText => $"{N(ObjectCount)} objects, {N(MeshCount)} meshes";

    public string VerticesText => N(VertexCount);

    public string MaterialsText => N(MaterialCount);

    public string TexturesText => _analysis.TextureCount == 0
        ? "none"
        : _analysis.MissingTextures.Count == 0
            ? $"{_analysis.TexturesFound} found" + (_analysis.TexturesEmbedded > 0 ? $" ({_analysis.TexturesEmbedded} embedded)" : "")
            : $"{_analysis.TexturesFound} found, {_analysis.MissingTextures.Count} missing";

    public IReadOnlyList<string> MissingTextures => _analysis.MissingTextures;

    public bool HasMissingTextures => _analysis.MissingTextures.Count > 0;

    public bool CanLocateTextures => _context.PickFolder is not null;

    public IReadOnlyList<string> Warnings => _analysis.Warnings;

    public bool HasWarnings => _analysis.Warnings.Count > 0;

    /// <summary>Size in Revit axes (width × depth × height) in metres for the chosen unit and up axis.</summary>
    public Vec3 SizeInMeters
    {
        get
        {
            var s = AxisConversion.SizeInRevitAxes(_analysis.SourceSize, SelectedUpAxis.Value);
            return s * SelectedUnit.Value.ToMeters();
        }
    }

    public string SizeText
    {
        get
        {
            var s = SizeInMeters;
            var unit = SelectedUnit.Value;
            var inUnit = s / unit.ToMeters();
            var metric = FormatLength(s.X) + " × " + FormatLength(s.Y) + " × " + FormatLength(s.Z);
            var source = string.Create(CultureInfo.InvariantCulture, $"{inUnit.X:0.###} × {inUnit.Y:0.###} × {inUnit.Z:0.###} {unit.Abbreviation()}");
            return $"{metric}   (W × D × H; {source})";
        }
    }

    public string SizeImperialText
    {
        get
        {
            var s = SizeInMeters / LengthUnits.MetersPerFoot;
            return $"{FeetInches(s.X)} × {FeetInches(s.Y)} × {FeetInches(s.Z)}";
        }
    }

    // ----- Options -------------------------------------------------------------------------------

    public IReadOnlyList<Option<LengthUnit>> Units { get; }

    public Option<LengthUnit> SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (Set(ref _selectedUnit, value))
            {
                OnSizeChanged();
                OnShortEdgesChanged();
            }
        }
    }

    public bool IsUnitDetected => _analysis.DetectedUnit is not null;

    public string UnitNote => _analysis.DetectedUnit is { } u
        ? $"From the file: {_analysis.UnitSource ?? u.DisplayName()}."
        : "The file does not say which unit it uses. Check that the size below looks right.";

    public IReadOnlyList<Option<UpAxis>> UpAxes { get; }

    public Option<UpAxis> SelectedUpAxis
    {
        get => _selectedUpAxis;
        set
        {
            if (Set(ref _selectedUpAxis, value))
            {
                OnSizeChanged();
            }
        }
    }

    public string UpAxisNote => $"Default for this file: {_analysis.DefaultUpAxis.DisplayName()} ({_analysis.UpAxisSource ?? "format convention"}).";

    public IReadOnlyList<Option<PlacementMode>> Placements { get; }

    public Option<PlacementMode> SelectedPlacement
    {
        get => _selectedPlacement;
        set => Set(ref _selectedPlacement, value);
    }

    public IReadOnlyList<CategoryOption> Categories { get; }

    public CategoryOption? SelectedCategory
    {
        get => _selectedCategory;
        set => Set(ref _selectedCategory, value);
    }

    public IReadOnlyList<Option<GroupingMode>> Groupings { get; }

    public Option<GroupingMode> SelectedGrouping
    {
        get => _selectedGrouping;
        set => Set(ref _selectedGrouping, value);
    }

    public bool CreateGroup
    {
        get => _createGroup;
        set => Set(ref _createGroup, value);
    }

    public IReadOnlyList<Option<GeometryMode>> GeometryModes { get; }

    public Option<GeometryMode> SelectedGeometryMode
    {
        get => _selectedGeometryMode;
        set => Set(ref _selectedGeometryMode, value);
    }

    public IReadOnlyList<Option<MaterialMode>> MaterialModeOptions { get; }

    public Option<MaterialMode> SelectedMaterialMode
    {
        get => _selectedMaterialMode;
        set
        {
            if (Set(ref _selectedMaterialMode, value))
            {
                OnPropertyChanged(nameof(MaterialModeExplanation));
                OnPropertyChanged(nameof(IsPaletteSizeVisible));
            }
        }
    }

    public string MaterialModeExplanation => SelectedMaterialMode.Value.Explanation();

    public string MaterialModeReason => $"Default for this model: {_analysis.RecommendedMaterialMode.DisplayName()}. {_analysis.MaterialModeReason}";

    public bool IsPaletteSizeVisible => SelectedMaterialMode.Value == MaterialMode.BakedColors;

    public int PaletteSize
    {
        get => _paletteSize;
        set => Set(ref _paletteSize, Math.Clamp(value, 2, 256));
    }

    public string TextureFolderText => string.IsNullOrEmpty(_context.TextureFolder)
        ? ""
        : _context.TextureFolderIsNextToProject
            ? $"Textures are copied to {_context.TextureFolder}. Keep this folder with the project."
            : $"The project is not saved yet, so textures are copied to {_context.TextureFolder}. Keep this folder with the project.";

    // ----- Simplify ------------------------------------------------------------------------------

    public bool IsSimplifyEnabled
    {
        get => _isSimplifyEnabled;
        set
        {
            if (Set(ref _isSimplifyEnabled, value))
            {
                OnEstimateChanged();
            }
        }
    }

    public SimplifyTargetKind SimplifyKind
    {
        get => _simplifyKind;
        set
        {
            if (Set(ref _simplifyKind, value))
            {
                OnPropertyChanged(nameof(IsPercentTarget));
                OnPropertyChanged(nameof(IsCountTarget));
                OnEstimateChanged();
            }
        }
    }

    public bool IsPercentTarget
    {
        get => SimplifyKind == SimplifyTargetKind.Ratio;
        set
        {
            if (value)
            {
                SimplifyKind = SimplifyTargetKind.Ratio;
            }
        }
    }

    public bool IsCountTarget
    {
        get => SimplifyKind == SimplifyTargetKind.TriangleCount;
        set
        {
            if (value)
            {
                SimplifyKind = SimplifyTargetKind.TriangleCount;
            }
        }
    }

    /// <summary>Percentage of faces to keep (0.1–100).</summary>
    public double SimplifyPercent
    {
        get => _simplifyPercent;
        set
        {
            if (Set(ref _simplifyPercent, Math.Clamp(value, 0.1, 100)))
            {
                OnEstimateChanged();
            }
        }
    }

    public long SimplifyTriangleCount
    {
        get => _simplifyTriangleCount;
        set
        {
            if (Set(ref _simplifyTriangleCount, Math.Max(4, value)))
            {
                OnEstimateChanged();
            }
        }
    }

    public SimplifyTarget? SimplifyTarget => IsSimplifyEnabled
        ? SimplifyKind == SimplifyTargetKind.Ratio
            ? Simplification.SimplifyTarget.Ratio(SimplifyPercent / 100.0)
            : Simplification.SimplifyTarget.Count(SimplifyTriangleCount)
        : null;

    /// <summary>Estimated faces after import, updated live as the target changes.</summary>
    public long EstimatedFaceCount
    {
        get
        {
            if (SimplifyTarget is not { } t)
            {
                return FaceCount;
            }

            var counts = _analysis.ObjectTriangleCounts.Count > 0 ? _analysis.ObjectTriangleCounts : [(int)Math.Min(int.MaxValue, FaceCount)];
            return TargetAllocator.Estimate(counts, t.ResolveTotal(FaceCount));
        }
    }

    public string EstimateText => IsSimplifyEnabled
        ? $"About {N(EstimatedFaceCount)} faces after simplifying ({(FaceCount == 0 ? 1.0 : (double)EstimatedFaceCount / FaceCount):P0} of {N(FaceCount)})."
        : $"{N(FaceCount)} faces will be imported.";

    // ----- Recommendation ------------------------------------------------------------------------

    public SimplificationRecommendation Recommendation =>
        SimplificationAdvisor.Evaluate(FaceCount, _settings.RecommendThreshold, _settings.StrongRecommendThreshold, _settings.ShowSimplifyRecommendation);

    /// <summary>The inline notice is shown whenever the face count is above the threshold, whether or not
    /// Simplify is already on (it then confirms the choice); it never blocks anything.</summary>
    public bool IsRecommendationVisible => Recommendation.IsVisible;

    public bool IsStrongRecommendation => Recommendation.Level == RecommendationLevel.StronglyRecommended;

    public string RecommendationTitle => SimplificationAdvisor.Title(Recommendation);

    public string RecommendationText => SimplificationAdvisor.Message(Recommendation);

    public long SuggestedFaceCount => Recommendation.SuggestedTriangles;

    public ICommand ApplyRecommendationCommand { get; }

    // ----- Short edges / units sanity ------------------------------------------------------------

    /// <summary>Estimated fraction of triangles Revit's short-curve tolerance would remove at the chosen unit.</summary>
    public double EstimatedShortEdgeFraction
    {
        get
        {
            var feetPerUnit = SelectedUnit.Value.ToFeet();
            if (!(feetPerUnit > 0))
            {
                return 0;
            }

            return _analysis.ShortestEdges.EstimateFractionBelow(_context.ShortCurveToleranceFeet / feetPerUnit);
        }
    }

    public bool IsShortEdgeWarningVisible => EstimatedShortEdgeFraction > _settings.ShortEdgeWarningFraction;

    public string ShortEdgeWarningText =>
        $"About {EstimatedShortEdgeFraction:P0} of the faces have edges shorter than Revit's minimum ({ShortCurveMillimeters:0.##} mm) at this unit and would be lost. The unit is probably wrong — the model would only be {SizeText.Split("   ")[0]}.";

    private double ShortCurveMillimeters => _context.ShortCurveToleranceFeet * LengthUnits.MetersPerFoot * 1000;

    // ----- Textures ------------------------------------------------------------------------------

    public ICommand LocateTexturesCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                ((RelayCommand)LocateTexturesCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public IReadOnlyList<string> TextureSearchFolders => _textureSearchFolders;

    // ----- Commands ------------------------------------------------------------------------------

    /// <summary>Always enabled; executing it closes the dialog with "import" and never asks for confirmation,
    /// whatever the face count and whether or not Simplify is on.</summary>
    public ICommand ImportCommand { get; }

    public ICommand CancelCommand { get; }

    public ImportChoices GetChoices() => new(
        SelectedUnit.Value,
        SelectedUpAxis.Value,
        SelectedPlacement.Value,
        SelectedCategory,
        SelectedGrouping.Value,
        CreateGroup,
        SelectedGeometryMode.Value,
        SelectedMaterialMode.Value,
        PaletteSize,
        SimplifyTarget,
        _textureSearchFolders);

    /// <summary>Remembers the user's choices as next time's defaults.</summary>
    public void SaveTo(ImporterSettings settings)
    {
        var last = settings.LastUsed;
        if (_analysis.DetectedUnit is null)
        {
            last.UnitByFormat[_analysis.FormatId] = SelectedUnit.Value;
        }

        last.UpAxisByFormat[_analysis.FormatId] = SelectedUpAxis.Value;
        last.Placement = SelectedPlacement.Value;
        if (SelectedCategory is not null)
        {
            last.Category = SelectedCategory.BuiltInName;
        }

        last.Grouping = SelectedGrouping.Value;
        last.CreateGroup = CreateGroup;
        last.GeometryMode = SelectedGeometryMode.Value;
        last.PaletteSize = PaletteSize;
        last.SimplifyEnabled = IsSimplifyEnabled;
        last.SimplifyKind = SimplifyKind;
        last.SimplifyPercent = SimplifyPercent;
        last.SimplifyTriangleCount = SimplifyTriangleCount;
        settings.TextureSearchFolders = _textureSearchFolders.Concat(settings.TextureSearchFolders).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
    }

    private void ApplyRecommendation()
    {
        var r = Recommendation;
        if (!r.IsVisible)
        {
            return;
        }

        SimplifyKind = SimplifyTargetKind.TriangleCount;
        SimplifyTriangleCount = r.SuggestedTriangles;
        SimplifyPercent = Math.Round(Math.Clamp(r.SuggestedRatio * 100, 0.1, 100), 1);
        IsSimplifyEnabled = true;
    }

    private async Task LocateTexturesAsync()
    {
        var folder = _context.PickFolder?.Invoke();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        if (!_textureSearchFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            _textureSearchFolders.Insert(0, folder);
        }

        if (_context.Reanalyze is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Looking for textures…";
        try
        {
            var before = _analysis.MissingTextures.Count;
            var updated = await _context.Reanalyze(_textureSearchFolders, CancellationToken.None).ConfigureAwait(true);
            UpdateAnalysis(updated);
            var found = before - updated.MissingTextures.Count;
            StatusMessage = found > 0 ? $"Found {found} more texture(s)." : "No missing textures were found in that folder.";
        }
        catch (Exception e) when (e is WorkerException or IOException or InvalidOperationException)
        {
            StatusMessage = "Could not check the folder: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Replaces the analysis (e.g. after locating textures). User choices are kept, except that the
    /// material mode follows the new recommendation if the user had not changed it.</summary>
    public void UpdateAnalysis(AnalysisResult updated)
    {
        var modeWasDefault = SelectedMaterialMode.Value == _analysis.RecommendedMaterialMode;
        _analysis = updated;
        if (modeWasDefault)
        {
            SelectedMaterialMode = MaterialModeOptions.First(m => m.Value == updated.RecommendedMaterialMode);
        }

        OnPropertyChanged(string.Empty);
    }

    private void Close(bool result)
    {
        DialogResult = result;
        CloseRequested?.Invoke(this, result);
    }

    private void OnSizeChanged()
    {
        OnPropertyChanged(nameof(SizeInMeters));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeImperialText));
        OnPropertyChanged(nameof(ShortEdgeWarningText));
    }

    private void OnShortEdgesChanged()
    {
        OnPropertyChanged(nameof(EstimatedShortEdgeFraction));
        OnPropertyChanged(nameof(IsShortEdgeWarningVisible));
        OnPropertyChanged(nameof(ShortEdgeWarningText));
    }

    private void OnEstimateChanged()
    {
        OnPropertyChanged(nameof(SimplifyTarget));
        OnPropertyChanged(nameof(EstimatedFaceCount));
        OnPropertyChanged(nameof(EstimateText));
    }

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    internal static string FormatLength(double meters)
    {
        var abs = Math.Abs(meters);
        return abs switch
        {
            >= 1000 => string.Create(CultureInfo.InvariantCulture, $"{meters / 1000:0.##} km"),
            >= 1 => string.Create(CultureInfo.InvariantCulture, $"{meters:0.##} m"),
            >= 0.01 => string.Create(CultureInfo.InvariantCulture, $"{meters * 100:0.##} cm"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{meters * 1000:0.###} mm"),
        };
    }

    internal static string FeetInches(double feet)
    {
        var totalInches = feet * 12;
        if (totalInches < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{totalInches:0.###}\"");
        }

        var ft = Math.Floor(totalInches / 12);
        var inches = totalInches - ft * 12;
        return ft > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ft:0}' {inches:0.#}\"")
            : string.Create(CultureInfo.InvariantCulture, $"{inches:0.#}\"");
    }
}
