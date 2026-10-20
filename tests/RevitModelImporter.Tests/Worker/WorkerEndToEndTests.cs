using System.Diagnostics;
using RevitModelImporter.Core.Intermediate;
using RevitModelImporter.Core.Presentation;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Settings;
using RevitModelImporter.Core.Simplification;
using RevitModelImporter.Core.Textures;
using RevitModelImporter.Core.Units;
using RevitModelImporter.Core.WorkerProtocol;
using RevitModelImporter.Tests.Support;
using Xunit.Abstractions;

namespace RevitModelImporter.Tests.Worker;

/// <summary>Runs the real worker process against generated fixtures, through the same WorkerClient the add-in uses.</summary>
[Collection(FixtureCollection.Name)]
public sealed class WorkerEndToEndTests : IDisposable
{
    private const double Feet = 1 / 0.3048;
    private readonly FixtureFactory _fixtures;
    private readonly ITestOutputHelper _output;
    private readonly string _out;

    public WorkerEndToEndTests(FixtureFactory fixtures, ITestOutputHelper output)
    {
        _fixtures = fixtures;
        _output = output;
        _out = Path.Combine(fixtures.Root, "out-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_out);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_out, true);
        }
        catch (IOException)
        {
        }
    }

    public static TheoryData<string> CubeFiles()
    {
        var data = new TheoryData<string>();
        foreach (var (_, file) in FixtureFactory.TexturedCubeExports)
        {
            data.Add(file);
        }

        return data;
    }

    [Fact]
    public async Task Formats_ListsRequiredFormats_AndLeavesOutRevitNativeOnes()
    {
        var formats = await WorkerHarness.CreateClient().GetFormatsAsync(CancellationToken.None);
        var extensions = formats.Formats.Select(f => f.Extension).ToHashSet();

        foreach (var required in new[] { "obj", "fbx", "gltf", "glb", "dae", "stl", "ply", "3ds", "3mf" })
        {
            Assert.Contains(required, extensions);
        }

        foreach (var native in new[] { "ifc", "ifczip", "dxf", "step", "stp", "skp", "3dm", "dwg", "sat" })
        {
            Assert.DoesNotContain(native, extensions);
        }

        Assert.StartsWith("6.", formats.AssimpVersion, StringComparison.Ordinal);
        var filter = FileFilterBuilder.Build(formats);
        Assert.Equal("All supported files", filter[0].Label);
        Assert.Contains("*.glb", filter[0].Patterns, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CubeFiles))]
    public void EveryFormat_AnalyzesAndConverts(string file)
    {
        var path = _fixtures[file];
        var analysis = WorkerHarness.Analyze(path);
        Assert.Equal(12, analysis.TriangleCount);
        Assert.Equal(1, analysis.ObjectCount);

        var unit = analysis.DetectedUnit ?? LengthUnit.Meters;
        var output = Path.Combine(_out, file + ".rmi");
        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = path,
            OutputPath = output,
            Unit = unit,
            UpAxis = analysis.DefaultUpAxis,
            MaterialMode = MaterialMode.FlatColors,
        });

        Assert.Equal(12, result.Stats.OutputTriangles);
        var model = IntermediateFile.Read(output);
        Assert.Single(model.Meshes);
        Assert.Equal(12, model.Meshes[0].Indices.Length / 3);
        Assert.NotEmpty(model.Header.Materials);

        // A unit cube in the detected/chosen unit.
        var size = unit.ToFeet();
        for (var axis = 0; axis < 3; axis++)
        {
            Assert.Equal(size, model.Header.BoundsMax[axis] - model.Header.BoundsMin[axis], 6);
        }
    }

    [Fact]
    public void ExternalTexture_TexturedMode_IsCopiedAndSizedFromUvs()
    {
        var textures = Path.Combine(_out, "tex");
        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures["cube.obj"],
            OutputPath = Path.Combine(_out, "cube.rmi"),
            Unit = LengthUnit.Meters,
            MaterialMode = MaterialMode.Textured,
            TextureDirectory = textures,
        });

        var model = IntermediateFile.Read(result.OutputPath);
        var brick = Assert.Single(model.Header.Materials, m => m.DiffuseTexture is not null);
        Assert.True(File.Exists(brick.DiffuseTexture));
        Assert.StartsWith(Path.Combine(textures, "brick_"), brick.DiffuseTexture, StringComparison.Ordinal);
        Assert.EndsWith(".png", brick.DiffuseTexture, StringComparison.Ordinal);
        // Each face maps UV 0..1 onto 1 m, so one texture repeat is 1 m.
        Assert.Equal(Feet, brick.TextureSizeX, 3);
        Assert.Equal(Feet, brick.TextureSizeY, 3);
        Assert.Equal(1, result.Stats.TexturesWritten);
        Assert.Empty(result.Stats.MissingTextures);

        // Re-importing writes nothing new (same content hash) and produces the same material key.
        var again = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures["cube.obj"],
            OutputPath = Path.Combine(_out, "cube2.rmi"),
            Unit = LengthUnit.Meters,
            MaterialMode = MaterialMode.Textured,
            TextureDirectory = textures,
        });
        Assert.Equal(0, again.Stats.TexturesWritten);
        Assert.Equal(brick.Key, IntermediateFile.Read(again.OutputPath).Header.Materials.Single(m => m.DiffuseTexture is not null).Key);
    }

    [Theory]
    [InlineData("cube.glb")]
    [InlineData("cube.fbx")]
    [InlineData("cube_ascii.fbx")]
    public void EmbeddedTexture_IsExtracted(string file)
    {
        var analysis = WorkerHarness.Analyze(_fixtures[file]);
        Assert.True(analysis.TexturesEmbedded == 1, Diagnose(file, analysis));

        var textures = Path.Combine(_out, "tex-" + file);
        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures[file],
            OutputPath = Path.Combine(_out, file + ".rmi"),
            Unit = analysis.DetectedUnit ?? LengthUnit.Meters,
            MaterialMode = MaterialMode.Textured,
            TextureDirectory = textures,
        });

        var material = Assert.Single(IntermediateFile.Read(result.OutputPath).Header.Materials, m => m.DiffuseTexture is not null);
        Assert.True(File.Exists(material.DiffuseTexture));
        Assert.Equal("png", ImageFormats.Sniff(File.ReadAllBytes(material.DiffuseTexture!)));
    }

    [Fact]
    public void BakedColors_UsesAPaletteOfFlatMaterials()
    {
        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures["cube.glb"],
            OutputPath = Path.Combine(_out, "baked.rmi"),
            MaterialMode = MaterialMode.BakedColors,
            PaletteSize = 4,
        });

        var materials = IntermediateFile.Read(result.OutputPath).Header.Materials;
        Assert.All(materials, m => Assert.Equal(MaterialKind.Baked, m.Kind));
        Assert.InRange(materials.Count, 1, 4);
        Assert.All(materials, m => Assert.Null(m.DiffuseTexture));
        Assert.Equal(12, materials.Sum(m => m.TriangleCount));
    }

    [Fact]
    public void FlatColors_UseTheTextureAverage()
    {
        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures["cube.glb"],
            OutputPath = Path.Combine(_out, "flat.rmi"),
            MaterialMode = MaterialMode.FlatColors,
        });

        // Half dark red, half near-white → a pinkish average, clearly not the white base colour.
        var brick = IntermediateFile.Read(result.OutputPath).Header.Materials.Single();
        Assert.True(brick.Color[0] > brick.Color[1] + 30, $"Expected a reddish average, got {string.Join(",", brick.Color)}");
    }

    [Fact]
    public void MultipleMaterialsOnOneObject_AreKept()
    {
        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures["multimaterial.glb"],
            OutputPath = Path.Combine(_out, "mm.rmi"),
            MaterialMode = MaterialMode.FlatColors,
        });

        var model = IntermediateFile.Read(result.OutputPath);
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(2, model.Header.Materials.Count);
        Assert.Equal(12, mesh.TriangleMaterials.Count(m => m == 0));
        Assert.Equal(12, mesh.TriangleMaterials.Count(m => m == 1));
        Assert.Contains(model.Header.Materials, m => m.Name.EndsWith("Red", StringComparison.Ordinal) && m.Color[0] > 150);
        Assert.Contains(model.Header.Materials, m => m.Name.EndsWith("Blue", StringComparison.Ordinal) && m.Color[2] > 150);
    }

    [Theory]
    [InlineData("nested.gltf")]
    [InlineData("nested.dae")]
    public void NestedTransforms_AndInstances_AreFlattenedIntoWorldSpace(string file)
    {
        var analysis = WorkerHarness.Analyze(_fixtures[file]);
        Assert.Equal(2, analysis.ObjectCount); // one mesh, two instances
        Assert.Equal(24, analysis.TriangleCount);
        Assert.Equal([0, 0, -2], analysis.BoundsMin.Select(v => Math.Round(v, 4)));
        Assert.Equal([17, 2, 1], analysis.BoundsMax.Select(v => Math.Round(v, 4)));

        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures[file],
            OutputPath = Path.Combine(_out, file + ".rmi"),
            Unit = LengthUnit.Meters,
            UpAxis = UpAxis.YUp,
        });

        // Y-up (x, y, z) → Revit (x, −z, y), metres → feet.
        var header = IntermediateFile.Read(result.OutputPath).Header;
        // (Float32 rotations in the files leave ~1e-6 ft of noise.)
        Assert.Equal([0, -1 * Feet, 0], header.BoundsMin, new ToleranceComparer(1e-4));
        Assert.Equal([17 * Feet, 2 * Feet, 2 * Feet], header.BoundsMax, new ToleranceComparer(1e-4));
    }

    [Fact]
    public void Merge_ProducesASingleObject()
    {
        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures["nested.gltf"],
            OutputPath = Path.Combine(_out, "merged.rmi"),
            Merge = true,
        });

        var model = IntermediateFile.Read(result.OutputPath);
        Assert.Equal(24, Assert.Single(model.Meshes).TriangleMaterials.Length);
    }

    [Fact]
    public void YUpModel_HeightEndsUpOnRevitZ()
    {
        var analysis = WorkerHarness.Analyze(_fixtures["tall_yup.glb"]);
        Assert.Equal(UpAxis.YUp, analysis.DefaultUpAxis);

        double Height(UpAxis up)
        {
            var r = WorkerHarness.Convert(new ConvertRequest { InputPath = _fixtures["tall_yup.glb"], OutputPath = Path.Combine(_out, $"tall-{up}.rmi"), UpAxis = up });
            var h = IntermediateFile.Read(r.OutputPath).Header;
            return (h.BoundsMax[2] - h.BoundsMin[2]) / Feet;
        }

        Assert.Equal(3.0, Height(UpAxis.YUp), 6);
        Assert.Equal(1.0, Height(UpAxis.ZUp), 6);
    }

    [Fact]
    public void CentimeterFbx_UnitIsDetectedAndApplied()
    {
        var analysis = WorkerHarness.Analyze(_fixtures["centimeters.fbx"]);
        Assert.Equal(LengthUnit.Centimeters, analysis.DetectedUnit);
        Assert.Contains("UnitScaleFactor", analysis.UnitSource, StringComparison.Ordinal);

        // No --units: the worker uses the detected unit. 200 × 100 × 50 cm, Y-up → W 2 m, D 0.5 m, H 1 m.
        var result = WorkerHarness.Run("convert", _fixtures["centimeters.fbx"], "--out", Path.Combine(_out, "cm.rmi"));
        Assert.Equal(0, result.ExitCode);
        var h = IntermediateFile.Read(Path.Combine(_out, "cm.rmi")).Header;
        Assert.Equal(2.0, (h.BoundsMax[0] - h.BoundsMin[0]) / Feet, 6);
        Assert.Equal(0.5, (h.BoundsMax[1] - h.BoundsMin[1]) / Feet, 6);
        Assert.Equal(1.0, (h.BoundsMax[2] - h.BoundsMin[2]) / Feet, 6);

        // OBJ has no unit: the dialog shows the size for whatever the user picks.
        var obj = WorkerHarness.Analyze(_fixtures["centimeters.obj"]);
        Assert.Null(obj.DetectedUnit);
        var vm = ViewModelFor(obj);
        vm.SelectedUnit = vm.Units.First(u => u.Value == LengthUnit.Centimeters);
        Assert.Equal(2.0, vm.SizeInMeters.X, 6);
        Assert.Equal(1.0, vm.SizeInMeters.Z, 6);
    }

    [Fact]
    public void TinyModel_WithWrongUnits_IsFlaggedBeforeAndAfterImport()
    {
        var analysis = WorkerHarness.Analyze(_fixtures["tiny_part_in_meters.obj"]);

        // In the dialog: read as millimetres, nearly every face is below Revit's 1/32" minimum edge.
        var vm = ViewModelFor(analysis);
        vm.SelectedUnit = vm.Units.First(u => u.Value == LengthUnit.Millimeters);
        Assert.True(vm.IsShortEdgeWarningVisible);
        Assert.True(vm.EstimatedShortEdgeFraction > 0.9);
        vm.SelectedUnit = vm.Units.First(u => u.Value == LengthUnit.Meters);
        Assert.False(vm.IsShortEdgeWarningVisible);

        // After conversion: the loss is counted and the warning names the unit problem.
        var wrong = WorkerHarness.Convert(new ConvertRequest { InputPath = _fixtures["tiny_part_in_meters.obj"], OutputPath = Path.Combine(_out, "tiny-mm.rmi"), Unit = LengthUnit.Millimeters });
        Assert.True(wrong.Stats.ShortEdgeTrianglesRemoved > analysis.TriangleCount / 2);
        Assert.Contains(wrong.Warnings, w => w.Contains("units are probably wrong", StringComparison.Ordinal));

        var right = WorkerHarness.Convert(new ConvertRequest { InputPath = _fixtures["tiny_part_in_meters.obj"], OutputPath = Path.Combine(_out, "tiny-m.rmi"), Unit = LengthUnit.Meters });
        Assert.Equal(0, right.Stats.ShortEdgeTrianglesRemoved);
        Assert.Equal(analysis.TriangleCount, right.Stats.OutputTriangles);
    }

    [Fact]
    public void LargeModel_TriggersRecommendation_WithoutForcingIt_AndSimplifiesToTheSuggestion()
    {
        var analysis = WorkerHarness.Analyze(_fixtures["sphere_300k.ply"]);
        Assert.InRange(analysis.TriangleCount, 280_000, 320_000);

        var vm = ViewModelFor(analysis);
        Assert.True(vm.IsRecommendationVisible);
        Assert.True(vm.IsStrongRecommendation);
        Assert.False(vm.IsSimplifyEnabled);
        Assert.True(vm.ImportCommand.CanExecute(null));

        vm.ApplyRecommendationCommand.Execute(null);
        var choices = vm.GetChoices();
        var target = Assert.IsType<SimplifyTarget>(choices.Simplify);

        var sw = Stopwatch.StartNew();
        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures["sphere_300k.ply"],
            OutputPath = Path.Combine(_out, "sphere.rmi"),
            Unit = LengthUnit.Meters,
            Simplify = target,
        });
        _output.WriteLine($"Converted {analysis.TriangleCount:N0} → {result.Stats.TrianglesAfterSimplify:N0} triangles in {sw.Elapsed.TotalSeconds:F1} s (stages: {string.Join(", ", result.Stats.StageMilliseconds.Select(kv => $"{kv.Key} {kv.Value} ms"))})");

        Assert.True(result.Stats.Simplified);
        Assert.InRange(result.Stats.TrianglesAfterSimplify, vm.SuggestedFaceCount * 0.95, vm.SuggestedFaceCount * 1.05);
        Assert.True(result.Stats.OutputTriangles < 50_000);
    }

    [Fact]
    public void SimplifyByRatio_HitsTheTarget()
    {
        var analysis = WorkerHarness.Analyze(_fixtures["sphere_300k.ply"]);
        var result = WorkerHarness.Run("convert", _fixtures["sphere_300k.ply"], "--out", Path.Combine(_out, "ratio.rmi"), "--units", "m", "--simplify", "10%");
        Assert.Equal(0, result.ExitCode);
        var after = result.Result!.Conversion!.Stats.TrianglesAfterSimplify;
        Assert.InRange(after, analysis.TriangleCount * 0.095, analysis.TriangleCount * 0.105);
    }

    [Fact]
    public void MissingTexture_IsReported_AndImportStillWorks()
    {
        var analysis = WorkerHarness.Analyze(_fixtures["missing_texture.obj"]);
        Assert.Equal(["missing_texture.png"], analysis.MissingTextures);
        Assert.Equal(MaterialMode.FlatColors, analysis.RecommendedMaterialMode);

        var result = WorkerHarness.Convert(new ConvertRequest
        {
            InputPath = _fixtures["missing_texture.obj"],
            OutputPath = Path.Combine(_out, "missing.rmi"),
            MaterialMode = MaterialMode.Textured,
            TextureDirectory = Path.Combine(_out, "tex-missing"),
        });

        Assert.Equal(["missing_texture.png"], result.Stats.MissingTextures);
        Assert.Equal(12, result.Stats.OutputTriangles);
        Assert.All(IntermediateFile.Read(result.OutputPath).Header.Materials, m => Assert.Null(m.DiffuseTexture));
    }

    [Fact]
    public void LocateTexturesFolder_FindsTexturesElsewhere_CaseInsensitively()
    {
        var elsewhere = Path.Combine(_out, "Somewhere Else");
        Directory.CreateDirectory(elsewhere);
        File.Copy(Path.Combine(_fixtures.Root, "textures", "brick.png"), Path.Combine(elsewhere, "MISSING_TEXTURE.PNG"));

        var analysis = WorkerHarness.Analyze(_fixtures["missing_texture.obj"], elsewhere);

        Assert.Empty(analysis.MissingTextures);
        Assert.Equal(1, analysis.TexturesFound);
        Assert.NotEqual(MaterialMode.FlatColors, analysis.RecommendedMaterialMode);
    }

    [Theory]
    [InlineData("corrupt_random.fbx")]
    [InlineData("corrupt_truncated.glb")]
    [InlineData("corrupt_chunks.3ds")]
    [InlineData("empty.stl")]
    public void CorruptFile_ReturnsParseError_AndTheCallerCarriesOn(string file)
    {
        var e = Assert.Throws<WorkerException>(() => WorkerHarness.Analyze(_fixtures[file]));
        Assert.Equal((int)WorkerExitCode.ParseFailed, e.ExitCode);
        Assert.Contains("could not be read", e.Message, StringComparison.Ordinal);

        // The calling process is fine and the worker still works.
        Assert.Equal(12, WorkerHarness.Analyze(_fixtures["cube.obj"]).TriangleCount);
    }

    [Fact]
    public void NativeCrashInTheWorker_IsReported_AndDoesNotTakeDownTheCaller()
    {
        var run = WorkerHarness.Run("--self-test-crash");
        Assert.NotEqual(0, run.ExitCode);
        Assert.False(WorkerExitCodes.IsKnown(run.ExitCode));
        Assert.Contains("stopped unexpectedly", WorkerExitCodes.Describe(run.ExitCode), StringComparison.Ordinal);

        // A real one: Assimp 6.0 segfaults on this malformed binary PLY (observed on Linux; on other platforms
        // the same file may fail differently). Either way the caller gets an exit code, not a crash.
        var real = WorkerHarness.Run("analyze", _fixtures["crash_assimp_malformed.ply"]);
        _output.WriteLine($"malformed PLY → exit code {real.ExitCode}");
        if (OperatingSystem.IsLinux())
        {
            Assert.NotEqual(0, real.ExitCode);
        }

        Assert.Equal(12, WorkerHarness.Analyze(_fixtures["cube.obj"]).TriangleCount);
    }

    [Fact]
    public void ErrorExitCodes_AreMeaningful()
    {
        Assert.Equal((int)WorkerExitCode.FileNotFound, WorkerHarness.Run("analyze", Path.Combine(_out, "nope.obj")).ExitCode);

        var ifc = Path.Combine(_out, "model.ifc");
        File.WriteAllText(ifc, "ISO-10303-21;");
        var unsupported = WorkerHarness.Run("analyze", ifc);
        Assert.Equal((int)WorkerExitCode.UnsupportedFormat, unsupported.ExitCode);
        Assert.Contains("Revit itself", unsupported.ErrorMessage, StringComparison.Ordinal);

        Assert.Equal((int)WorkerExitCode.UsageError, WorkerHarness.Run("convert", _fixtures["cube.obj"]).ExitCode);
        Assert.Equal((int)WorkerExitCode.UsageError, WorkerHarness.Run("frobnicate").ExitCode);
        Assert.Equal((int)WorkerExitCode.UsageError, WorkerHarness.Run("convert", _fixtures["cube.obj"], "--out", Path.Combine(_out, "x.rmi"), "--units", "furlongs").ExitCode);
    }

    [Fact]
    public async Task Cancel_KillsTheWorkerImmediately()
    {
        var client = WorkerHarness.CreateClient();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var progress = new SyncProgress(_ => started.TrySetResult());
        var request = new ConvertRequest
        {
            InputPath = _fixtures["sphere_300k.ply"],
            OutputPath = Path.Combine(_out, "cancelled.rmi"),
            Simplify = SimplifyTarget.Ratio(0.01),
        };

        var task = client.ConvertAsync(request, progress, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        _output.WriteLine($"Cancelled after {sw.ElapsedMilliseconds} ms: {e.GetType().Name}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(request.OutputPath));
    }

    /// <summary>Failure details for CI logs: what the worker saw and the texture-related lines of the file.</summary>
    private string Diagnose(string file, AnalysisResult a)
    {
        var path = _fixtures[file];
        var text = File.ReadAllText(path, System.Text.Encoding.Latin1);
        var lines = text.Split('\n')
            .Where(l => l.Contains("Video", StringComparison.Ordinal) || l.Contains("Filename", StringComparison.OrdinalIgnoreCase) || l.Contains("Content", StringComparison.Ordinal) || l.Contains("Texture", StringComparison.Ordinal))
            .Select(l => l.Trim().Length > 120 ? l.Trim()[..120] + "…" : l.Trim())
            .Take(30);
        return $"{file}: size={new FileInfo(path).Length} textures={a.TextureCount} found={a.TexturesFound} embedded={a.TexturesEmbedded} missing=[{string.Join(", ", a.MissingTextures)}] materials={a.MaterialCount} mode={a.RecommendedMaterialMode} warnings=[{string.Join(" | ", a.Warnings)}]\n" + string.Join("\n", lines);
    }

    private static ImportDialogViewModel ViewModelFor(AnalysisResult analysis) =>
        new(analysis, new ImporterSettings(), new ImportDialogContext { Categories = [new CategoryOption(-2000151, "Generic Models", "OST_GenericModel")] });

    private sealed class SyncProgress(Action<WorkerProgress> onReport) : IProgress<WorkerProgress>
    {
        public void Report(WorkerProgress value) => onReport(value);
    }

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;

        public int GetHashCode(double obj) => 0;
    }
}
