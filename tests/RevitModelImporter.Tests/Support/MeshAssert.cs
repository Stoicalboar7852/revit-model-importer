using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Processing;

namespace RevitModelImporter.Tests.Support;

public static class MeshAssert
{
    /// <summary>No triangle has repeated vertices or (near) zero area.</summary>
    public static void NoDegenerateTriangles(ObjectMesh mesh)
    {
        var diag = mesh.ComputeBounds().Diagonal;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = mesh.Indices[3 * t];
            var b = mesh.Indices[3 * t + 1];
            var c = mesh.Indices[3 * t + 2];
            Assert.True(a != b && b != c && a != c, $"Triangle {t} repeats a vertex.");
            var area = Vec3.Cross(mesh.GetVertex(b) - mesh.GetVertex(a), mesh.GetVertex(c) - mesh.GetVertex(a)).Length;
            Assert.True(area > 1e-14 * diag * diag, $"Triangle {t} has zero area.");
        }
    }

    /// <summary>Every edge is shared by at most two triangles and, when shared, used in opposite directions
    /// (consistent orientation: no flipped triangles).</summary>
    public static void ConsistentlyOriented(ObjectMesh mesh)
    {
        var directed = new HashSet<(int, int)>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            for (var e = 0; e < 3; e++)
            {
                var a = mesh.Indices[3 * t + e];
                var b = mesh.Indices[3 * t + (e + 1) % 3];
                Assert.True(directed.Add((a, b)), $"Directed edge {a}->{b} is used twice: a triangle is flipped or duplicated.");
            }
        }
    }

    /// <summary>For a closed surface around the origin, every face normal points away from the center.</summary>
    public static void NormalsPointOutward(ObjectMesh mesh, Vec3 center)
    {
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = mesh.GetVertex(mesh.Indices[3 * t]);
            var b = mesh.GetVertex(mesh.Indices[3 * t + 1]);
            var c = mesh.GetVertex(mesh.Indices[3 * t + 2]);
            var n = Vec3.Cross(b - a, c - a);
            var centroid = (a + b + c) / 3.0;
            Assert.True(Vec3.Dot(n, centroid - center) > 0, $"Triangle {t} faces inward.");
        }
    }
}
