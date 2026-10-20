using RevitModelImporter.Core.Presentation;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Settings;
using RevitModelImporter.Core.Simplification;
using RevitModelImporter.Core.Statistics;
using RevitModelImporter.Core.Units;
using RevitModelImporter.Core.WorkerProtocol;

namespace RevitModelImporter.Tests.Core;

/// <summary>One test per "recommend simplifying, never force it" rule, plus the rest of the dialog logic.</summary>
public class ImportDialogViewModelTests
{
    private static readonly CategoryOption GenericModels = new(-2000151, "Generic Models", "OST_GenericModel");
    private static readonly CategoryOption Furniture = new(-2000080, "Furniture", "OST_Furniture");

    private static AnalysisResult Analysis(long triangles, string format = "obj", LengthUnit? unit = null, params int[] objectTriangles) => new()
    {
        FilePath = "/models/model." + format,
        FormatId = format,
        TriangleCount = triangles,
        SourcePolygonCount = triangles,
        ObjectCount = Math.Max(1, objectTriangles.Length),
        MeshCount = Math.Max(1, objectTriangles.Length),
        VertexCount = triangles / 2,
        MaterialCount = 1,
        ObjectTriangleCounts = objectTriangles.Length > 0 ? [.. objectTriangles] : [(int)triangles],
        BoundsMin = [0, 0, 0],
        BoundsMax = [2, 3, 4],
        DetectedUnit = unit,
        DefaultUpAxis = UpAxis.YUp,
        RecommendedMaterialMode = MaterialMode.FlatColors,
        MaterialModeReason = "The model has no textures.",
    };

    private static ImportDialogViewModel Create(AnalysisResult analysis, ImporterSettings? settings = null) =>
        new(analysis, settings ?? new ImporterSettings(), new ImportDialogContext { Categories = [Furniture, GenericModels] });

    // ---- Rule: faces = triangles after triangulation, and that number is shown ------------------------------

    [Fact]
    public void FaceCount_IsTheTriangleCount_AndIsShown()
    {
        var a = Analysis(60_000);
        a.SourcePolygonCount = 30_000; // quads
        var vm = Create(a);

        Assert.Equal(60_000, vm.FaceCount);
        Assert.Equal("60,000 faces (triangles)", vm.FaceCountText);
        Assert.Contains("30,000 polygons", vm.PolygonNote, StringComparison.Ordinal);
    }

    // ---- Rule: inline notice above the threshold, not at or below it ---------------------------------------

    [Theory]
    [InlineData(50_001, true)]
    [InlineData(1_000_000, true)]
    [InlineData(50_000, false)]
    [InlineData(12, false)]
    public void Notice_IsShownOnlyAboveTheThreshold(long faces, bool visible)
    {
        var vm = Create(Analysis(faces));
        Assert.Equal(visible, vm.IsRecommendationVisible);
    }

    [Fact]
    public void Notice_SaysHowManyFaces_WhyItHurts_AndSuggestsATarget()
    {
        var vm = Create(Analysis(120_000));
        var text = vm.RecommendationText;

        Assert.Contains("120,000 faces", text, StringComparison.Ordinal);
        Assert.Contains("slower to draw and regenerate", text, StringComparison.Ordinal);
        Assert.Contains("project file larger", text, StringComparison.Ordinal);
        Assert.Contains("45,000 faces", text, StringComparison.Ordinal);
        Assert.Contains("You can still import it as it is", text, StringComparison.Ordinal);
        Assert.Equal("Simplifying is recommended", vm.RecommendationTitle);
    }

    [Fact]
    public void SuggestedTarget_BringsTheModelUnderTheThreshold()
    {
        foreach (var faces in new long[] { 50_001, 75_000, 300_000, 5_000_000 })
        {
            var vm = Create(Analysis(faces));
            Assert.True(vm.SuggestedFaceCount < 50_000, $"{faces} → {vm.SuggestedFaceCount}");
            Assert.True(vm.SuggestedFaceCount > 0);
        }
    }

    // ---- Rule: "Apply recommendation" turns Simplify on with the suggested target ---------------------------

    [Fact]
    public void ApplyRecommendation_TurnsOnSimplifyWithTheSuggestedTarget()
    {
        var vm = Create(Analysis(300_000));
        Assert.True(vm.ApplyRecommendationCommand.CanExecute(null));

        vm.ApplyRecommendationCommand.Execute(null);

        Assert.True(vm.IsSimplifyEnabled);
        Assert.Equal(SimplifyTargetKind.TriangleCount, vm.SimplifyKind);
        Assert.Equal(vm.SuggestedFaceCount, vm.SimplifyTriangleCount);
        Assert.Equal(SimplifyTarget.Count(vm.SuggestedFaceCount), vm.GetChoices().Simplify);
        Assert.InRange(vm.EstimatedFaceCount, vm.SuggestedFaceCount * 0.95, vm.SuggestedFaceCount * 1.05);
    }

    // ---- Rule: Simplify is never switched on silently ------------------------------------------------------

    [Fact]
    public void Simplify_StartsOff_EvenForAHugeModel()
    {
        var vm = Create(Analysis(5_000_000));

        Assert.True(vm.IsStrongRecommendation);
        Assert.False(vm.IsSimplifyEnabled);
        Assert.Null(vm.GetChoices().Simplify);
        Assert.Equal(5_000_000, vm.EstimatedFaceCount);
    }

    [Fact]
    public void Simplify_StartsInTheLastUsedState()
    {
        var settings = new ImporterSettings();
        settings.LastUsed.SimplifyEnabled = true;
        settings.LastUsed.SimplifyKind = SimplifyTargetKind.Ratio;
        settings.LastUsed.SimplifyPercent = 40;

        var vm = Create(Analysis(10_000), settings);

        Assert.True(vm.IsSimplifyEnabled);
        Assert.Equal(SimplifyTarget.Ratio(0.4), vm.GetChoices().Simplify);
        Assert.False(vm.IsRecommendationVisible);
    }

    [Fact]
    public void Simplify_LastUsedOff_StaysOff()
    {
        var settings = new ImporterSettings();
        settings.LastUsed.SimplifyEnabled = false;
        var vm = Create(Analysis(400_000), settings);
        Assert.False(vm.IsSimplifyEnabled);
    }

    // ---- Rule: Import is always enabled and never asks "Are you sure?" ---------------------------------------

    [Theory]
    [InlineData(10, false)]
    [InlineData(300_000, false)]
    [InlineData(10_000_000, false)]
    [InlineData(300_000, true)]
    public void Import_IsAlwaysEnabled_AndClosesImmediately(long faces, bool simplify)
    {
        var vm = Create(Analysis(faces));
        vm.IsSimplifyEnabled = simplify;
        bool? closedWith = null;
        vm.CloseRequested += (_, ok) => closedWith = ok;

        Assert.True(vm.ImportCommand.CanExecute(null));
        vm.ImportCommand.Execute(null);

        Assert.True(closedWith);
        Assert.True(vm.DialogResult);
    }

    [Fact]
    public void Import_HasNoConfirmationHook()
    {
        // The context offers no way to ask for confirmation; the only callbacks are folder picking and re-analysis.
        var callbacks = typeof(ImportDialogContext).GetProperties().Where(p => p.PropertyType.IsSubclassOf(typeof(Delegate))).Select(p => p.Name).ToList();
        Assert.Equal(["PickFolder", "Reanalyze"], callbacks.Order());
    }

    // ---- Rule: stronger wording above the second threshold, still non-blocking -------------------------------

    [Fact]
    public void AboveTheStrongThreshold_TheWordingIsStronger_ButNothingIsBlocked()
    {
        var normal = Create(Analysis(100_000));
        var strong = Create(Analysis(400_000));

        Assert.False(normal.IsStrongRecommendation);
        Assert.True(strong.IsStrongRecommendation);
        Assert.Equal("Simplifying is strongly recommended", strong.RecommendationTitle);
        Assert.Contains("far above 250,000", strong.RecommendationText, StringComparison.Ordinal);
        Assert.DoesNotContain("far above", normal.RecommendationText, StringComparison.Ordinal);
        Assert.True(strong.ImportCommand.CanExecute(null));
        Assert.False(strong.IsSimplifyEnabled);
    }

    [Fact]
    public void Thresholds_ComeFromSettings()
    {
        var settings = new ImporterSettings { RecommendThreshold = 1_000, StrongRecommendThreshold = 2_000 };
        Assert.True(Create(Analysis(1_500), settings).IsRecommendationVisible);
        Assert.False(Create(Analysis(1_500), settings).IsStrongRecommendation);
        Assert.True(Create(Analysis(2_500), settings).IsStrongRecommendation);
        Assert.False(Create(Analysis(900), settings).IsRecommendationVisible);
    }

    // ---- Rule: any model can be simplified, and the notice can be turned off ---------------------------------

    [Fact]
    public void ASmallModel_CanStillBeSimplified()
    {
        var vm = Create(Analysis(10_000));
        Assert.False(vm.IsRecommendationVisible);

        vm.IsSimplifyEnabled = true;
        vm.SimplifyKind = SimplifyTargetKind.Ratio;
        vm.SimplifyPercent = 50;

        Assert.Equal(SimplifyTarget.Ratio(0.5), vm.GetChoices().Simplify);
        Assert.Equal(5_000, vm.EstimatedFaceCount);
    }

    [Fact]
    public void Recommendation_CanBeTurnedOffInSettings()
    {
        var settings = new ImporterSettings { ShowSimplifyRecommendation = false };
        var vm = Create(Analysis(1_000_000), settings);

        Assert.False(vm.IsRecommendationVisible);
        Assert.False(vm.IsStrongRecommendation);
        Assert.Equal("", vm.RecommendationText);
        Assert.False(vm.ApplyRecommendationCommand.CanExecute(null));
    }

    // ---- Live estimate --------------------------------------------------------------------------------------

    [Fact]
    public void Estimate_UpdatesLive_AndRaisesPropertyChanged()
    {
        var vm = Create(Analysis(100_000, "obj", null, 90_000, 10_000));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsSimplifyEnabled = true;
        vm.SimplifyKind = SimplifyTargetKind.Ratio;
        vm.SimplifyPercent = 10;
        Assert.Equal(10_000, vm.EstimatedFaceCount);

        vm.SimplifyKind = SimplifyTargetKind.TriangleCount;
        vm.SimplifyTriangleCount = 20_000;
        Assert.Equal(20_000, vm.EstimatedFaceCount);
        Assert.Contains("20,000", vm.EstimateText, StringComparison.Ordinal);
        Assert.Contains(nameof(vm.EstimatedFaceCount), changed);
        Assert.Contains(nameof(vm.EstimateText), changed);
    }

    [Fact]
    public void Estimate_KeepsAMinimumPerObject()
    {
        // 1,000 tiny objects of 12 triangles + one big one: at 1 % the small parts keep their 12 triangles.
        var counts = Enumerable.Repeat(12, 1_000).Append(100_000).ToArray();
        var vm = Create(Analysis(counts.Sum(), "obj", null, counts));
        vm.IsSimplifyEnabled = true;
        vm.SimplifyKind = SimplifyTargetKind.Ratio;
        vm.SimplifyPercent = 1;

        Assert.True(vm.EstimatedFaceCount >= 12_000);
    }

    // ---- Units, axes, size -------------------------------------------------------------------------------

    [Fact]
    public void Units_DetectedUnitIsUsed_OtherwiseLastUsedForThatFormat_OtherwiseAGuess()
    {
        Assert.Equal(LengthUnit.Centimeters, Create(Analysis(10, "fbx", LengthUnit.Centimeters)).SelectedUnit.Value);
        Assert.Equal(LengthUnit.Millimeters, Create(Analysis(10, "stl")).SelectedUnit.Value);
        Assert.Equal(LengthUnit.Meters, Create(Analysis(10, "obj")).SelectedUnit.Value);

        var settings = new ImporterSettings();
        settings.LastUsed.UnitByFormat["obj"] = LengthUnit.Inches;
        var vm = Create(Analysis(10, "obj"), settings);
        Assert.Equal(LengthUnit.Inches, vm.SelectedUnit.Value);
        Assert.False(vm.IsUnitDetected);
        Assert.Contains("does not say", vm.UnitNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Size_ReflectsUnitAndUpAxis()
    {
        // Source box 2 × 3 × 4 (x, y, z).
        var vm = Create(Analysis(10, "obj"));
        vm.SelectedUnit = vm.Units.First(u => u.Value == LengthUnit.Meters);
        vm.SelectedUpAxis = vm.UpAxes.First(a => a.Value == UpAxis.YUp);
        Assert.Equal(new RevitModelImporter.Core.Geometry.Vec3(2, 4, 3), vm.SizeInMeters);
        Assert.StartsWith("2 m × 4 m × 3 m", vm.SizeText, StringComparison.Ordinal);

        vm.SelectedUpAxis = vm.UpAxes.First(a => a.Value == UpAxis.ZUp);
        vm.SelectedUnit = vm.Units.First(u => u.Value == LengthUnit.Millimeters);
        Assert.StartsWith("2 mm × 3 mm × 4 mm", vm.SizeText, StringComparison.Ordinal);
        Assert.Contains("\"", vm.SizeImperialText, StringComparison.Ordinal);
    }

    // ---- Other options ------------------------------------------------------------------------------------

    [Fact]
    public void Category_DefaultsToGenericModels_AndRemembersTheLastChoice()
    {
        Assert.Equal(GenericModels, Create(Analysis(10)).SelectedCategory);

        var settings = new ImporterSettings();
        settings.LastUsed.Category = "OST_Furniture";
        Assert.Equal(Furniture, Create(Analysis(10), settings).SelectedCategory);
    }

    [Fact]
    public void MaterialMode_DefaultsToTheModelsRecommendation_WithAnExplanation()
    {
        var a = Analysis(10);
        a.RecommendedMaterialMode = MaterialMode.BakedColors;
        a.MaterialModeReason = "Atlas.";
        var vm = Create(a);

        Assert.Equal(MaterialMode.BakedColors, vm.SelectedMaterialMode.Value);
        Assert.True(vm.IsPaletteSizeVisible);
        Assert.Contains("palette", vm.MaterialModeExplanation, StringComparison.Ordinal);
        Assert.Contains("Atlas.", vm.MaterialModeReason, StringComparison.Ordinal);

        vm.SelectedMaterialMode = vm.MaterialModeOptions.First(m => m.Value == MaterialMode.Textured);
        Assert.False(vm.IsPaletteSizeVisible);
        Assert.Contains("will not line up", vm.MaterialModeExplanation, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveTo_RemembersChoicesForNextTime()
    {
        var settings = new ImporterSettings();
        var vm = Create(Analysis(10, "stl"), settings);
        vm.SelectedUnit = vm.Units.First(u => u.Value == LengthUnit.Inches);
        vm.SelectedPlacement = vm.Placements.First(p => p.Value == PlacementMode.CenterOnOrigin);
        vm.SelectedCategory = Furniture;
        vm.SelectedGrouping = vm.Groupings.First(g => g.Value == GroupingMode.SingleElement);
        vm.SelectedGeometryMode = vm.GeometryModes.First(g => g.Value == GeometryMode.MeshOnly);
        vm.IsSimplifyEnabled = true;
        vm.SimplifyPercent = 33;
        vm.PaletteSize = 16;

        vm.SaveTo(settings);

        var last = settings.LastUsed;
        Assert.Equal(LengthUnit.Inches, last.UnitByFormat["stl"]);
        Assert.Equal(PlacementMode.CenterOnOrigin, last.Placement);
        Assert.Equal("OST_Furniture", last.Category);
        Assert.Equal(GroupingMode.SingleElement, last.Grouping);
        Assert.Equal(GeometryMode.MeshOnly, last.GeometryMode);
        Assert.True(last.SimplifyEnabled);
        Assert.Equal(33, last.SimplifyPercent);
        Assert.Equal(16, last.PaletteSize);

        // Detected units are not stored as a preference.
        var fbx = Create(Analysis(10, "fbx", LengthUnit.Centimeters), settings);
        fbx.SaveTo(settings);
        Assert.False(settings.LastUsed.UnitByFormat.ContainsKey("fbx"));
    }

    [Fact]
    public async Task LocateTextures_ReanalyzesWithTheChosenFolder()
    {
        var a = Analysis(10);
        a.TextureCount = 1;
        a.MissingTextures = ["brick.png"];
        IReadOnlyList<string>? searched = null;
        var reanalyzed = new TaskCompletionSource();
        var context = new ImportDialogContext
        {
            Categories = [GenericModels],
            PickFolder = () => "/somewhere/textures",
            Reanalyze = (folders, _) =>
            {
                searched = folders;
                var b = Analysis(10);
                b.TextureCount = 1;
                b.TexturesFound = 1;
                b.RecommendedMaterialMode = MaterialMode.BakedColors;
                reanalyzed.SetResult();
                return Task.FromResult(b);
            },
        };
        var vm = new ImportDialogViewModel(a, new ImporterSettings(), context);
        Assert.True(vm.HasMissingTextures);

        vm.LocateTexturesCommand.Execute(null);
        await reanalyzed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();

        Assert.Equal(["/somewhere/textures"], searched);
        Assert.False(vm.HasMissingTextures);
        Assert.Equal(MaterialMode.BakedColors, vm.SelectedMaterialMode.Value);
        Assert.Contains("/somewhere/textures", vm.GetChoices().TextureSearchFolders);
    }

    [Fact]
    public void Cancel_ClosesWithFalse()
    {
        var vm = Create(Analysis(10));
        bool? result = null;
        vm.CloseRequested += (_, ok) => result = ok;
        vm.CancelCommand.Execute(null);
        Assert.False(result);
    }
}

public class SimplificationAdvisorTests
{
    [Theory]
    [InlineData(10, RecommendationLevel.None)]
    [InlineData(50_000, RecommendationLevel.None)]
    [InlineData(50_001, RecommendationLevel.Recommended)]
    [InlineData(250_000, RecommendationLevel.Recommended)]
    [InlineData(250_001, RecommendationLevel.StronglyRecommended)]
    public void Levels(long faces, RecommendationLevel level) =>
        Assert.Equal(level, SimplificationAdvisor.Evaluate(faces, 50_000, 250_000).Level);

    [Fact]
    public void Disabled_MeansNoRecommendation() =>
        Assert.Equal(RecommendationLevel.None, SimplificationAdvisor.Evaluate(10_000_000, 50_000, 250_000, enabled: false).Level);

    [Fact]
    public void BrokenThresholds_AreTolerated()
    {
        // Threshold clamps to 1 and the strong threshold to the threshold, so everything above is "strong".
        var r = SimplificationAdvisor.Evaluate(100, 0, -5);
        Assert.Equal(RecommendationLevel.StronglyRecommended, r.Level);
        Assert.InRange(r.SuggestedTriangles, 1, 100);
    }

    [Theory]
    [InlineData(1_000_000, 50_000, 45_000)]
    [InlineData(300_000, 100_000, 90_000)]
    [InlineData(60_000, 51_234, 46_000)]
    public void SuggestedTarget_IsRoundAndUnderTheThreshold(long faces, long threshold, long expected) =>
        Assert.Equal(expected, SimplificationAdvisor.SuggestTarget(faces, threshold));
}
