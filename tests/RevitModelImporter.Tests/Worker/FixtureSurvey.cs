using RevitModelImporter.Tests.Support;
using Xunit.Abstractions;

namespace RevitModelImporter.Tests.Worker;

/// <summary>Prints what the worker sees in every generated fixture (diagnostics for the test log).</summary>
[Collection(FixtureCollection.Name)]
public class FixtureSurvey
{
    private readonly FixtureFactory _fixtures;
    private readonly ITestOutputHelper _output;

    public FixtureSurvey(FixtureFactory fixtures, ITestOutputHelper output)
    {
        _fixtures = fixtures;
        _output = output;
    }

    [Fact]
    public void Survey()
    {
        foreach (var file in Directory.GetFiles(_fixtures.Root).OrderBy(f => f))
        {
            var run = WorkerHarness.Run("analyze", file);
            var a = run.Result?.Analysis;
            _output.WriteLine(a is null
                ? $"{Path.GetFileName(file),-28} exit={run.ExitCode} error={run.ErrorMessage}"
                : $"{Path.GetFileName(file),-28} exit={run.ExitCode} obj={a.ObjectCount} mesh={a.MeshCount} tri={a.TriangleCount} poly={a.SourcePolygonCount} vtx={a.VertexCount} mat={a.MaterialCount} tex={a.TextureCount}/{a.TexturesFound}/{a.TexturesEmbedded} missing=[{string.Join(",", a.MissingTextures)}] unit={a.DetectedUnit} up={a.DefaultUpAxis} uv={a.HasTexCoords} mode={a.RecommendedMaterialMode} min=[{string.Join(",", a.BoundsMin.Select(v => v.ToString("0.###")))}] max=[{string.Join(",", a.BoundsMax.Select(v => v.ToString("0.###")))}] warn={string.Join(" | ", a.Warnings)}");
        }
    }
}
