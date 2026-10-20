using System.Diagnostics;
using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Simplification;
using RevitModelImporter.Tests.Support;
using Xunit.Abstractions;

namespace RevitModelImporter.Tests.Core;

public class QuadricSimplifierTests
{
    private readonly ITestOutputHelper _output;

    public QuadricSimplifierTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(20_000, 0.5)]
    [InlineData(20_000, 0.1)]
    [InlineData(50_000, 0.02)]
    public void Sphere_ReachesTargetWithinFivePercent(int triangles, double ratio)
    {
        var mesh = TestMeshes.Sphere(triangles);
        var target = (int)(mesh.TriangleCount * ratio);

        var result = new QuadricSimplifier().Simplify(mesh, target);

        Assert.InRange(mesh.TriangleCount, target * 0.95, target * 1.05);
        Assert.Equal(result.OutputTriangles, mesh.TriangleCount);
        Assert.True(result.TargetReached);
    }

    [Fact]
    public void Sphere_BoundingBoxStaysWithinOnePercent()
    {
        var mesh = TestMeshes.Sphere(40_000, radius: 3.0, center: new Vec3(10, -4, 2));
        var before = mesh.ComputeBounds();

        new QuadricSimplifier().Simplify(mesh, mesh.TriangleCount / 10);

        var after = mesh.ComputeBounds();
        var tolerance = 0.01 * before.Diagonal;
        Assert.True((after.Min - before.Min).Length <= tolerance, $"min moved {(after.Min - before.Min).Length}");
        Assert.True((after.Max - before.Max).Length <= tolerance, $"max moved {(after.Max - before.Max).Length}");
    }

    [Fact]
    public void Sphere_HasNoDegenerateOrFlippedTriangles()
    {
        var center = new Vec3(1, 2, 3);
        var mesh = TestMeshes.Sphere(30_000, center: center);
        MeshAssert.NormalsPointOutward(mesh, center);

        new QuadricSimplifier().Simplify(mesh, 1_000);

        MeshAssert.NoDegenerateTriangles(mesh);
        MeshAssert.ConsistentlyOriented(mesh);
        MeshAssert.NormalsPointOutward(mesh, center);
    }

    [Fact]
    public void OpenGrid_BorderIsPreserved()
    {
        // A bumpy open sheet: the interior can be simplified, the outline must stay exactly where it was.
        var mesh = TestMeshes.Grid(80, size: 2.0, bumpAmplitude: 0.05);
        var borderBefore = BorderVertices(mesh);

        new QuadricSimplifier().Simplify(mesh, mesh.TriangleCount / 8);

        Assert.True(mesh.TriangleCount < 80 * 80 * 2 / 4, "The interior should still simplify.");
        var bounds = mesh.ComputeBounds();
        Assert.Equal(0.0, bounds.Min.X, 9);
        Assert.Equal(0.0, bounds.Min.Y, 9);
        Assert.Equal(2.0, bounds.Max.X, 9);
        Assert.Equal(2.0, bounds.Max.Y, 9);

        // Every border vertex of the result is an original border vertex, and every border edge
        // lies on the original outline (one of the four sides of the square).
        var borderAfter = BorderEdges(mesh);
        Assert.NotEmpty(borderAfter);
        foreach (var (a, b) in borderAfter)
        {
            var pa = mesh.GetVertex(a);
            var pb = mesh.GetVertex(b);
            Assert.Contains(Key(pa), borderBefore);
            Assert.Contains(Key(pb), borderBefore);
            Assert.True(OnSameSide(pa, pb, 2.0), $"Border edge {pa} – {pb} cuts across the sheet.");
        }

        MeshAssert.NoDegenerateTriangles(mesh);
        MeshAssert.ConsistentlyOriented(mesh);
    }

    [Fact]
    public void MaterialSeam_IsPreserved()
    {
        // Flat two-material sheet: the seam is the line x = 1.
        var mesh = TestMeshes.Grid(60, size: 2.0, twoMaterials: true);

        new QuadricSimplifier().Simplify(mesh, 200);

        Assert.True(mesh.TriangleCount < 2_000);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var mat = mesh.TriangleMaterials[t];
            for (var c = 0; c < 3; c++)
            {
                var x = mesh.GetVertex(mesh.Indices[3 * t + c]).X;
                if (mat == 0)
                {
                    Assert.True(x <= 1.0 + 1e-9, $"Material 0 triangle {t} crosses the seam (x={x}).");
                }
                else
                {
                    Assert.True(x >= 1.0 - 1e-9, $"Material 1 triangle {t} crosses the seam (x={x}).");
                }
            }
        }

        // Both materials still cover their half of the sheet.
        Assert.Equal(2.0, Area(mesh, 0), 6);
        Assert.Equal(2.0, Area(mesh, 1), 6);
    }

    [Fact]
    public void Uvs_AreCarriedThroughCollapses()
    {
        // On a flat grid the UV is a linear function of position: u = x / size, v = y / size.
        var mesh = TestMeshes.Grid(50, size: 1.0);

        new QuadricSimplifier().Simplify(mesh, 300);

        Assert.NotNull(mesh.CornerUVs);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            for (var c = 0; c < 3; c++)
            {
                var p = mesh.GetVertex(mesh.Indices[3 * t + c]);
                Assert.Equal(p.X, mesh.CornerUVs![6 * t + 2 * c], 3);
                Assert.Equal(p.Y, mesh.CornerUVs[6 * t + 2 * c + 1], 3);
            }
        }
    }

    [Fact]
    public void OneMillionTriangles_ReducedToTenPercent_InAFewSeconds()
    {
        var mesh = TestMeshes.Sphere(1_000_000);
        var input = mesh.TriangleCount;
        var target = input / 10;

        var sw = Stopwatch.StartNew();
        new QuadricSimplifier().Simplify(mesh, target);
        sw.Stop();

        _output.WriteLine($"{input:N0} → {mesh.TriangleCount:N0} triangles in {sw.Elapsed.TotalSeconds:F2} s");
        Assert.InRange(mesh.TriangleCount, target * 0.95, target * 1.05);
        // "A few seconds" on a CI runner; typically ~2–3 s on a modern desktop.
        Assert.True(sw.Elapsed.TotalSeconds < 15, $"Took {sw.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void TargetAboveInput_LeavesMeshUntouched()
    {
        var mesh = TestMeshes.Sphere(1_000);
        var before = mesh.TriangleCount;
        var result = new QuadricSimplifier().Simplify(mesh, before + 10);
        Assert.Equal(before, mesh.TriangleCount);
        Assert.Equal(0, result.Iterations);
    }

    private static (long, long, long) Key(Vec3 p) =>
        ((long)Math.Round(p.X * 1e7), (long)Math.Round(p.Y * 1e7), (long)Math.Round(p.Z * 1e7));

    private static HashSet<(long, long, long)> BorderVertices(ObjectMesh mesh)
    {
        var set = new HashSet<(long, long, long)>();
        foreach (var (a, b) in BorderEdges(mesh))
        {
            set.Add(Key(mesh.GetVertex(a)));
            set.Add(Key(mesh.GetVertex(b)));
        }

        return set;
    }

    private static List<(int, int)> BorderEdges(ObjectMesh mesh)
    {
        var count = new Dictionary<(int, int), int>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            for (var e = 0; e < 3; e++)
            {
                var a = mesh.Indices[3 * t + e];
                var b = mesh.Indices[3 * t + (e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        }

        return count.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
    }

    private static bool OnSameSide(Vec3 a, Vec3 b, double size)
    {
        const double eps = 1e-9;
        return (Math.Abs(a.X) < eps && Math.Abs(b.X) < eps)
            || (Math.Abs(a.Y) < eps && Math.Abs(b.Y) < eps)
            || (Math.Abs(a.X - size) < eps && Math.Abs(b.X - size) < eps)
            || (Math.Abs(a.Y - size) < eps && Math.Abs(b.Y - size) < eps);
    }

    private static double Area(ObjectMesh mesh, int material)
    {
        var area = 0.0;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            if (mesh.TriangleMaterials[t] != material)
            {
                continue;
            }

            var a = mesh.GetVertex(mesh.Indices[3 * t]);
            var b = mesh.GetVertex(mesh.Indices[3 * t + 1]);
            var c = mesh.GetVertex(mesh.Indices[3 * t + 2]);
            area += 0.5 * Vec3.Cross(b - a, c - a).Length;
        }

        return area;
    }
}
