using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Processing;

namespace RevitModelImporter.Tests.Support;

/// <summary>Procedural meshes for tests. Nothing is downloaded.</summary>
public static class TestMeshes
{
    /// <summary>Closed UV sphere with roughly <paramref name="approxTriangles"/> triangles and UVs.</summary>
    public static ObjectMesh Sphere(int approxTriangles, double radius = 1.0, Vec3 center = default)
    {
        // Triangles ≈ 2 * segments * (rings - 1); use segments = 2 * rings.
        var rings = Math.Max(3, (int)Math.Round(Math.Sqrt(approxTriangles / 4.0)));
        var segments = 2 * rings;
        var positions = new List<double>();
        var uvsPerVertex = new List<(float U, float V)>();
        for (var r = 0; r <= rings; r++)
        {
            var phi = Math.PI * r / rings;
            for (var s = 0; s <= segments; s++)
            {
                var theta = 2 * Math.PI * s / segments;
                positions.Add(center.X + radius * Math.Sin(phi) * Math.Cos(theta));
                positions.Add(center.Y + radius * Math.Sin(phi) * Math.Sin(theta));
                positions.Add(center.Z + radius * Math.Cos(phi));
                uvsPerVertex.Add(((float)s / segments, 1f - (float)r / rings));
            }
        }

        var indices = new List<int>();
        int V(int r, int s) => r * (segments + 1) + s;
        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                var a = V(r, s);
                var b = V(r + 1, s);
                var c = V(r + 1, s + 1);
                var d = V(r, s + 1);
                if (r != 0)
                {
                    indices.AddRange([a, b, d]);
                }

                if (r != rings - 1)
                {
                    indices.AddRange([b, c, d]);
                }
            }
        }

        return Build("Sphere", positions, indices, uvsPerVertex, weld: true);
    }

    /// <summary>Flat n×n grid in the XY plane (open borders). Optionally the right half uses material 1.</summary>
    public static ObjectMesh Grid(int n, double size = 1.0, bool twoMaterials = false, double bumpAmplitude = 0.0)
    {
        var positions = new List<double>();
        var uvs = new List<(float, float)>();
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                var px = size * x / n;
                var py = size * y / n;
                positions.Add(px);
                positions.Add(py);
                positions.Add(bumpAmplitude * Math.Sin(px * 7.1) * Math.Cos(py * 5.3));
                uvs.Add(((float)x / n, (float)y / n));
            }
        }

        var indices = new List<int>();
        var mats = new List<int>();
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var a = y * (n + 1) + x;
                var b = a + 1;
                var c = a + n + 2;
                var d = a + n + 1;
                var m = twoMaterials && x >= n / 2 ? 1 : 0;
                indices.AddRange([a, b, c]);
                indices.AddRange([a, c, d]);
                mats.Add(m);
                mats.Add(m);
            }
        }

        var mesh = Build("Grid", positions, indices, uvs, weld: false);
        mesh.TriangleMaterials = mats.ToArray();
        return mesh;
    }

    private static ObjectMesh Build(string name, List<double> positions, List<int> indices, List<(float U, float V)> uvs, bool weld)
    {
        var pos = positions.ToArray();
        var idx = indices.ToArray();
        var cornerUvs = new float[idx.Length * 2];
        for (var i = 0; i < idx.Length; i++)
        {
            cornerUvs[2 * i] = uvs[idx[i]].U;
            cornerUvs[2 * i + 1] = uvs[idx[i]].V;
        }

        if (weld)
        {
            // Merge the seam/pole duplicates of the UV sphere so it is closed.
            var map = new Dictionary<(long, long, long), int>();
            var newPos = new List<double>();
            var remap = new int[pos.Length / 3];
            for (var v = 0; v < remap.Length; v++)
            {
                var key = ((long)Math.Round(pos[3 * v] * 1e9), (long)Math.Round(pos[3 * v + 1] * 1e9), (long)Math.Round(pos[3 * v + 2] * 1e9));
                if (!map.TryGetValue(key, out var ni))
                {
                    ni = newPos.Count / 3;
                    newPos.AddRange([pos[3 * v], pos[3 * v + 1], pos[3 * v + 2]]);
                    map.Add(key, ni);
                }

                remap[v] = ni;
            }

            for (var i = 0; i < idx.Length; i++)
            {
                idx[i] = remap[idx[i]];
            }

            pos = newPos.ToArray();
        }

        return new ObjectMesh
        {
            Name = name,
            NodePath = name,
            Positions = pos,
            Indices = idx,
            TriangleMaterials = new int[idx.Length / 3],
            CornerUVs = cornerUvs,
        };
    }
}
