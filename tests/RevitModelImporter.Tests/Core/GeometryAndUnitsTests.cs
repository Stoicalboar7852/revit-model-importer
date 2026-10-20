using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Scene;
using RevitModelImporter.Core.Units;

namespace RevitModelImporter.Tests.Core;

public class UnitsTests
{
    [Theory]
    [InlineData(LengthUnit.Millimeters, 0.001)]
    [InlineData(LengthUnit.Centimeters, 0.01)]
    [InlineData(LengthUnit.Meters, 1)]
    [InlineData(LengthUnit.Inches, 0.0254)]
    [InlineData(LengthUnit.Feet, 0.3048)]
    public void ToMeters(LengthUnit unit, double meters) => Assert.Equal(meters, unit.ToMeters(), 12);

    [Fact]
    public void FeetIsRevitsInternalUnit()
    {
        Assert.Equal(1.0, LengthUnit.Feet.ToFeet(), 12);
        Assert.Equal(1 / 0.3048, LengthUnit.Meters.ToFeet(), 12);
        Assert.Equal(1.0 / 12, LengthUnit.Inches.ToFeet(), 12);
    }

    [Theory]
    [InlineData("mm", LengthUnit.Millimeters)]
    [InlineData("Centimeters", LengthUnit.Centimeters)]
    [InlineData("metre", LengthUnit.Meters)]
    [InlineData("in", LengthUnit.Inches)]
    [InlineData("foot", LengthUnit.Feet)]
    public void Parse(string text, LengthUnit expected)
    {
        Assert.True(LengthUnits.TryParse(text, out var u));
        Assert.Equal(expected, u);
    }

    [Fact]
    public void FromMetersPerUnit_MatchesKnownUnitsOnly()
    {
        Assert.Equal(LengthUnit.Centimeters, LengthUnits.FromMetersPerUnit(0.01));
        Assert.Equal(LengthUnit.Inches, LengthUnits.FromMetersPerUnit(0.0254));
        Assert.Null(LengthUnits.FromMetersPerUnit(0.0123));
        Assert.Null(LengthUnits.FromMetersPerUnit(0));
    }

    [Fact]
    public void YUpToZUp_IsARightHandedRotation()
    {
        var m = AxisConversion.ToZUp(UpAxis.YUp);
        Assert.Equal(new Vec3(0, 0, 1), m.TransformPoint(new Vec3(0, 1, 0)));
        Assert.Equal(new Vec3(0, -1, 0), m.TransformPoint(new Vec3(0, 0, 1)));
        Assert.Equal(new Vec3(1, 0, 0), m.TransformPoint(new Vec3(1, 0, 0)));
        Assert.Equal(1.0, m.Determinant3x3, 12);
        Assert.True(AxisConversion.ToZUp(UpAxis.ZUp).IsIdentity);
    }

    [Fact]
    public void SourceToRevit_ScalesToFeet()
    {
        var p = AxisConversion.SourceToRevit(UpAxis.YUp, LengthUnit.Millimeters).TransformPoint(new Vec3(304.8, 609.6, 0));
        Assert.Equal(1.0, p.X, 9);
        Assert.Equal(0.0, p.Y, 9);
        Assert.Equal(2.0, p.Z, 9);
    }

    [Fact]
    public void SizeInRevitAxes_SwapsHeightForYUp() =>
        Assert.Equal(new Vec3(1, 3, 2), AxisConversion.SizeInRevitAxes(new Vec3(1, 2, 3), UpAxis.YUp));
}

public class SceneFlattenerTests
{
    private static SceneMesh Triangle(int material = 0) => new()
    {
        Name = "Tri",
        Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0],
        Indices = [0, 1, 2],
        TexCoords = [0, 0, 1, 0, 0, 1],
        MaterialIndex = material,
    };

    [Fact]
    public void NestedTransforms_AreAccumulated()
    {
        var scene = new SceneModel();
        scene.Meshes.Add(Triangle());
        var parent = new SceneNode { Name = "Parent", LocalTransform = Mat4.Translation(10, 0, 0) };
        var child = new SceneNode { Name = "Child", LocalTransform = Mat4.Scale(2) };
        child.MeshIndices.Add(0);
        parent.Children.Add(child);
        scene.Root.Children.Add(parent);

        var obj = Assert.Single(SceneFlattener.Flatten(scene, Mat4.Identity));

        Assert.Equal("Child", obj.Name);
        Assert.Equal("Root/Parent/Child", obj.NodePath);
        Assert.Equal(new Vec3(10, 0, 0), obj.GetVertex(obj.Indices[0]));
        Assert.Equal(new Vec3(12, 0, 0), obj.GetVertex(obj.Indices[1]));
    }

    [Fact]
    public void InstancedMesh_BecomesOneObjectPerInstance()
    {
        var scene = new SceneModel();
        scene.Meshes.Add(Triangle());
        for (var i = 0; i < 3; i++)
        {
            var n = new SceneNode { Name = $"I{i}", LocalTransform = Mat4.Translation(i * 5, 0, 0) };
            n.MeshIndices.Add(0);
            scene.Root.Children.Add(n);
        }

        var objects = SceneFlattener.Flatten(scene, Mat4.Identity);
        Assert.Equal(3, objects.Count);
        Assert.Equal(10, objects[2].ComputeBounds().Min.X);
    }

    [Fact]
    public void MirroringTransform_FlipsWindingSoNormalsStayOutward()
    {
        var scene = new SceneModel();
        scene.Meshes.Add(Triangle());
        var n = new SceneNode { LocalTransform = Mat4.Scale(-1, 1, 1) };
        n.MeshIndices.Add(0);
        scene.Root.Children.Add(n);

        var obj = SceneFlattener.Flatten(scene, Mat4.Identity)[0];
        var a = obj.GetVertex(obj.Indices[0]);
        var normal = Vec3.Cross(obj.GetVertex(obj.Indices[1]) - a, obj.GetVertex(obj.Indices[2]) - a);
        Assert.True(normal.Z > 0, "The +Z facing triangle must still face +Z after mirroring in X.");
        // UVs follow their vertices.
        Assert.Equal(0f, obj.CornerUVs![2]);
        Assert.Equal(1f, obj.CornerUVs[3]);
    }

    [Fact]
    public void MeshesOfOneNode_AreCombined_AndShareSeamVertices()
    {
        var scene = new SceneModel();
        scene.Meshes.Add(Triangle(0));
        scene.Meshes.Add(new SceneMesh { Positions = [1, 0, 0, 1, 1, 0, 0, 1, 0], Indices = [0, 1, 2], MaterialIndex = 1 });
        var n = new SceneNode { Name = "Both" };
        n.MeshIndices.AddRange([0, 1]);
        scene.Root.Children.Add(n);

        var obj = Assert.Single(SceneFlattener.Flatten(scene, Mat4.Identity));
        Assert.Equal(2, obj.TriangleCount);
        Assert.Equal(4, obj.VertexCount); // the shared edge's two vertices are welded
        Assert.Equal([0, 1], obj.TriangleMaterials);
        Assert.NotNull(obj.CornerUVs); // one mesh has UVs, the other gets zeros
    }
}

public class MeshCleanupTests
{
    private static ObjectMesh Mesh(double[] positions, int[] indices) => new()
    {
        Positions = positions,
        Indices = indices,
        TriangleMaterials = new int[indices.Length / 3],
    };

    [Fact]
    public void WeldsVerticesWithinTolerance()
    {
        var mesh = Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0, 1.0000001, 0, 0, 1, 1, 0], [0, 1, 2, 3, 4, 2]);
        var report = MeshCleanup.Clean(mesh, new CleanupOptions(1e-4, 0));
        Assert.Equal(1, report.WeldedVertices);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(2, mesh.TriangleCount);
    }

    [Fact]
    public void CollapsesShortEdges_AndCountsTheLoss()
    {
        // A sliver (0.0001 wide) next to a normal triangle.
        var mesh = Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 0.0001, 0], [0, 1, 2, 1, 3, 2]);
        var report = MeshCleanup.Clean(mesh, new CleanupOptions(1e-9, 0.001));
        Assert.Equal(1, report.ShortEdgeTriangles);
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(1, report.OutputTriangles);
    }

    [Fact]
    public void RemovesDegenerateAndDuplicateTriangles()
    {
        var mesh = Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 0, 0], [0, 1, 2, 2, 1, 0, 0, 1, 3, 0, 0, 1]);
        var report = MeshCleanup.Clean(mesh, new CleanupOptions(1e-9, 0));
        Assert.Equal(1, report.DuplicateTriangles); // (2,1,0) is (0,1,2) again
        Assert.Equal(2, report.DegenerateTriangles); // collinear (0,1,3) and repeated index (0,0,1)
        Assert.Equal(1, mesh.TriangleCount);
    }

    [Fact]
    public void AModelSmallerThanTheTolerance_DisappearsAndIsCountedAsTooSmall()
    {
        var mesh = Mesh([0, 0, 0, 1e-5, 0, 0, 0, 1e-5, 0], [0, 1, 2]);
        var report = MeshCleanup.Clean(mesh, CleanupOptions.RevitDefaults);
        Assert.Equal(0, mesh.TriangleCount);
        Assert.Equal(1, report.ShortEdgeTriangles);
        Assert.Equal(0, report.DegenerateTriangles);
    }

    [Fact]
    public void DoesNotMergeSeparateSurfacesThatAreMerelyClose()
    {
        // Two parallel triangles 0.002 apart (less than the 0.0026 ft min edge): no edge is short, so nothing merges.
        var mesh = Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0.002, 1, 0, 0.002, 0, 1, 0.002], [0, 1, 2, 3, 4, 5]);
        var report = MeshCleanup.Clean(mesh, new CleanupOptions(0.0005, 0.0026));
        Assert.Equal(0, report.RemovedTriangles);
        Assert.Equal(2, mesh.TriangleCount);
    }

    [Fact]
    public void EdgeHistogram_EstimatesLossPerUnit()
    {
        var h = new RevitModelImporter.Core.Statistics.EdgeLengthHistogram();
        for (var i = 0; i < 90; i++)
        {
            h.Add(10); // 10 source units
        }

        for (var i = 0; i < 10; i++)
        {
            h.Add(0.1);
        }

        Assert.Equal(0.0, h.EstimateFractionBelow(0.05), 3);
        Assert.Equal(0.1, h.EstimateFractionBelow(1), 3);
        Assert.Equal(1.0, h.EstimateFractionBelow(100), 3);
    }
}

public class TargetAllocatorTests
{
    [Fact]
    public void SplitsProportionally()
    {
        var t = RevitModelImporter.Core.Simplification.TargetAllocator.Allocate([1000, 3000], 400);
        Assert.Equal([100, 300], t);
    }

    [Fact]
    public void KeepsAMinimumPerMesh_AndTakesItFromTheBigOnes()
    {
        var t = RevitModelImporter.Core.Simplification.TargetAllocator.Allocate([20, 20, 10_000], 1_000, minimumPerMesh: 12);
        Assert.Equal(12, t[0]);
        Assert.Equal(12, t[1]);
        Assert.Equal(976, t[2]);
    }

    [Fact]
    public void NeverExceedsTheMesh_AndHandlesEdgeCases()
    {
        Assert.Equal([5, 3], RevitModelImporter.Core.Simplification.TargetAllocator.Allocate([5, 3], 2, 12));
        Assert.Equal([10, 20], RevitModelImporter.Core.Simplification.TargetAllocator.Allocate([10, 20], 1_000));
        Assert.Empty(RevitModelImporter.Core.Simplification.TargetAllocator.Allocate([], 10));
    }

    [Theory]
    [InlineData("0.25", RevitModelImporter.Core.Simplification.SimplifyTargetKind.Ratio, 0.25)]
    [InlineData("25%", RevitModelImporter.Core.Simplification.SimplifyTargetKind.Ratio, 0.25)]
    [InlineData("50000", RevitModelImporter.Core.Simplification.SimplifyTargetKind.TriangleCount, 50000)]
    public void SimplifyTarget_Parses(string text, RevitModelImporter.Core.Simplification.SimplifyTargetKind kind, double value)
    {
        Assert.True(RevitModelImporter.Core.Simplification.SimplifyTarget.TryParse(text, out var t));
        Assert.Equal(kind, t.Kind);
        Assert.Equal(value, t.Value, 9);
        Assert.True(RevitModelImporter.Core.Simplification.SimplifyTarget.TryParse(t.ToArgument(), out var back));
        Assert.Equal(t, back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-3")]
    [InlineData("150%")]
    [InlineData("abc")]
    public void SimplifyTarget_RejectsNonsense(string text) =>
        Assert.False(RevitModelImporter.Core.Simplification.SimplifyTarget.TryParse(text, out _));
}
