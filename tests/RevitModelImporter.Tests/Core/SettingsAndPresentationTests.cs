using RevitModelImporter.Core.Presentation;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Settings;
using RevitModelImporter.Core.Units;
using RevitModelImporter.Core.WorkerProtocol;

namespace RevitModelImporter.Tests.Core;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rmi-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void MissingFile_GivesDefaults()
    {
        var s = new SettingsStore(Path.Combine(_dir, "none.json")).Load();
        Assert.True(s.ShowSimplifyRecommendation);
        Assert.Equal(50_000, s.RecommendThreshold);
        Assert.Equal(250_000, s.StrongRecommendThreshold);
        Assert.False(s.LastUsed.SimplifyEnabled);
        Assert.Equal("OST_GenericModel", s.LastUsed.Category);
    }

    [Fact]
    public void RoundTrips()
    {
        var store = new SettingsStore(Path.Combine(_dir, "sub", "settings.json"));
        var s = new ImporterSettings { RecommendThreshold = 1234, StrongRecommendThreshold = 5678, ShowSimplifyRecommendation = false, FallbackTextureFolder = "/tex" };
        s.LastUsed.UnitByFormat["stl"] = LengthUnit.Inches;
        s.LastUsed.SimplifyEnabled = true;
        s.LastUsed.Placement = PlacementMode.ProjectBasePoint;
        store.Save(s);

        var loaded = store.Load();
        Assert.Equal(1234, loaded.RecommendThreshold);
        Assert.Equal(5678, loaded.StrongRecommendThreshold);
        Assert.False(loaded.ShowSimplifyRecommendation);
        Assert.Equal("/tex", loaded.FallbackTextureFolder);
        Assert.Equal(LengthUnit.Inches, loaded.LastUsed.UnitByFormat["STL"]);
        Assert.True(loaded.LastUsed.SimplifyEnabled);
        Assert.Equal(PlacementMode.ProjectBasePoint, loaded.LastUsed.Placement);
        Assert.Contains("\"recommendThreshold\": 1234", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptFile_GivesDefaults_AndKeepsACopy()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path);
        var s = store.Load();
        Assert.Equal(50_000, s.RecommendThreshold);
        Assert.NotNull(store.LastError);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void HandEditedNonsense_IsClamped()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "recommendThreshold": -5, "strongRecommendThreshold": 3, "maxTrianglesPerBuild": 1, "lastUsed": { "paletteSize": 9999 } }""");
        var s = new SettingsStore(path).Load();
        Assert.Equal(1, s.RecommendThreshold);
        Assert.Equal(3, s.StrongRecommendThreshold);
        Assert.Equal(100, s.MaxTrianglesPerBuild);
        Assert.Equal(256, s.LastUsed.PaletteSize);
    }
}

public class SettingsViewModelTests
{
    [Fact]
    public void Save_WritesValuesBack()
    {
        var settings = new ImporterSettings();
        var vm = new SettingsViewModel(settings, "/x/settings.json");
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;
        vm.ShowRecommendation = false;
        vm.Threshold = 10_000;
        vm.StrongThreshold = 20_000;
        vm.SaveCommand.Execute(null);

        Assert.True(closed);
        Assert.False(settings.ShowSimplifyRecommendation);
        Assert.Equal(10_000, settings.RecommendThreshold);
        Assert.Equal(20_000, settings.StrongRecommendThreshold);
    }

    [Fact]
    public void InvalidThresholds_CannotBeSaved()
    {
        var vm = new SettingsViewModel(new ImporterSettings(), "/x/settings.json");
        vm.Threshold = 100;
        vm.StrongThreshold = 50;
        Assert.False(vm.IsValid);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Contains("strong threshold", vm.ValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_RestoresDefaults()
    {
        var vm = new SettingsViewModel(new ImporterSettings { RecommendThreshold = 7, ShowSimplifyRecommendation = false }, "/x");
        vm.ResetCommand.Execute(null);
        Assert.Equal(50_000, vm.Threshold);
        Assert.True(vm.ShowRecommendation);
    }
}

public class PresentationHelpersTests
{
    [Theory]
    [InlineData("Wood {oak} [1]", "Wood _oak_ _1_")]
    [InlineData("a|b;c<d>e?f`g~h\\i", "a_b_c_d_e_f_g_h_i")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("", "Imported")]
    [InlineData("tab\there", "tab here")]
    public void RevitNaming_Sanitizes(string input, string expected) => Assert.Equal(expected, RevitNaming.Sanitize(input));

    [Fact]
    public void RevitNaming_MakesUnique()
    {
        var taken = new HashSet<string> { "Wood", "Wood (2)" };
        Assert.Equal("Wood (3)", RevitNaming.MakeUnique("Wood", taken.Contains));
        Assert.Equal("Metal", RevitNaming.MakeUnique("Metal", taken.Contains));
        Assert.True(RevitNaming.Sanitize(new string('x', 500)).Length <= RevitNaming.MaxLength);
    }

    [Fact]
    public void FileFilter_IsBuiltFromTheWorkersList()
    {
        var formats = new FormatsResult
        {
            Formats =
            [
                new FormatInfo { Extension = "obj", Description = "Wavefront Object" },
                new FormatInfo { Extension = "gltf", Description = "glTF2" },
                new FormatInfo { Extension = "glb", Description = "glTF2" },
                new FormatInfo { Extension = "x3d", Description = "Extensible 3D(X3D, X3DB)" },
            ],
        };

        var filters = FileFilterBuilder.Build(formats);

        Assert.Equal(("All supported files", "*.obj;*.gltf;*.glb;*.x3d"), filters[0]);
        Assert.Contains(("Wavefront Object (*.obj)", "*.obj"), filters);
        Assert.Contains(("glTF2 (*.gltf;*.glb)", "*.gltf;*.glb"), filters);
        Assert.Contains(filters, f => f.Patterns == "*.x3d");
        Assert.Equal("*.*", filters[^1].Patterns);
        Assert.DoesNotContain(filters, f => f.Patterns.Contains("*.fbx", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_ListsWhatTheBriefAsksFor()
    {
        var s = new ImportSummary
        {
            FileName = "chair.fbx",
            ElementsCreated = 3,
            MaterialsCreated = 2,
            MaterialsReused = 1,
            SourceTriangles = 100_000,
            Simplified = true,
            TrianglesAfterSimplify = 40_000,
            TrianglesSentToRevit = 39_900,
            TrianglesRejectedByRevit = 12,
            MissingTextures = ["wood.jpg"],
            TextureFolder = "/p/x_textures",
            AnalysisTime = TimeSpan.FromSeconds(1),
            ConversionTime = TimeSpan.FromSeconds(2),
            CreationTime = TimeSpan.FromSeconds(3),
            Warnings = ["careful"],
        };

        var text = s.ToText();
        foreach (var expected in new[] { "Elements created: 3", "2 created, 1 reused", "100,000", "40,000", "Faces Revit rejected (estimate): 12", "wood.jpg", "6.0 s", "careful", "keep this folder with the project" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        Assert.Contains("cancelled", new ImportSummary { Cancelled = true }.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerMessages_RoundTripAsJsonLines()
    {
        var line = new WorkerMessage { Type = WorkerMessage.ProgressType, Stage = "Loading", Fraction = 0.5, Message = "a < b" }.ToJsonLine();
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        var back = WorkerMessage.TryParse(line);
        Assert.Equal("Loading", back!.Stage);
        Assert.Equal("a < b", back.Message);
        Assert.Null(WorkerMessage.TryParse("garbage"));
        Assert.Null(WorkerMessage.TryParse("{ broken"));
    }

    [Fact]
    public void ExitCodes_HaveUserFacingDescriptions()
    {
        foreach (var code in Enum.GetValues<WorkerExitCode>())
        {
            Assert.False(string.IsNullOrWhiteSpace(WorkerExitCodes.Describe((int)code)));
        }

        Assert.Contains("crash", WorkerExitCodes.Describe(unchecked((int)0xC0000005)), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertRequest_MapsToTheWorkerCommandLine()
    {
        var args = new ConvertRequest
        {
            InputPath = "in.obj",
            OutputPath = "out.rmi",
            Unit = LengthUnit.Centimeters,
            UpAxis = UpAxis.ZUp,
            MaterialMode = MaterialMode.BakedColors,
            Simplify = RevitModelImporter.Core.Simplification.SimplifyTarget.Ratio(0.25),
            TextureDirectory = "tex",
            TextureSearchFolders = ["a", "b"],
            VertexTolerance = 0.0005,
            MinEdgeLength = 0.0026,
            Merge = true,
        }.ToArguments();

        Assert.Equal(
            ["convert", "in.obj", "--out", "out.rmi", "--units", "cm", "--up", "z", "--material-mode", "baked", "--palette-size", "32", "--simplify", "0.25", "--texture-dir", "tex", "--texture-search", "a", "--texture-search", "b", "--vertex-tolerance", "0.0005", "--min-edge", "0.0026", "--merge"],
            args);
    }
}
