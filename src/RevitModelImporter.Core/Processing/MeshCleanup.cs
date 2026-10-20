using RevitModelImporter.Core.Geometry;

namespace RevitModelImporter.Core.Processing;

/// <param name="VertexTolerance">Vertices closer than this are welded (Revit: Application.VertexTolerance).</param>
/// <param name="MinEdgeLength">Edges shorter than this are collapsed; triangles that still have such an edge are
/// dropped (Revit: Application.ShortCurveTolerance).</param>
public sealed record CleanupOptions(double VertexTolerance, double MinEdgeLength)
{
    /// <summary>Revit 2025–2027 defaults in feet, used when the add-in did not pass its runtime values.</summary>
    public static CleanupOptions RevitDefaults { get; } = new(0.0005233832795, 0.00256026455729167);
}

public sealed class CleanupReport
{
    public long InputTriangles { get; set; }

    public long OutputTriangles { get; set; }

    public long WeldedVertices { get; set; }

    public long DegenerateTriangles { get; set; }

    public long DuplicateTriangles { get; set; }

    /// <summary>Triangles lost because they had an edge shorter than the minimum edge length.</summary>
    public long ShortEdgeTriangles { get; set; }

    public long RemovedTriangles => DegenerateTriangles + DuplicateTriangles + ShortEdgeTriangles;

    public void Add(CleanupReport o)
    {
        InputTriangles += o.InputTriangles;
        OutputTriangles += o.OutputTriangles;
        WeldedVertices += o.WeldedVertices;
        DegenerateTriangles += o.DegenerateTriangles;
        DuplicateTriangles += o.DuplicateTriangles;
        ShortEdgeTriangles += o.ShortEdgeTriangles;
    }
}

/// <summary>Prepares meshes for Revit: welds coincident vertices, collapses edges shorter than Revit's
/// short-curve tolerance (keeping the mesh connected), and removes degenerate and duplicate triangles.</summary>
public static class MeshCleanup
{
    public static CleanupReport Clean(ObjectMesh mesh, CleanupOptions options)
    {
        var report = new CleanupReport { InputTriangles = mesh.TriangleCount };
        var n = mesh.VertexCount;
        if (n == 0 || mesh.TriangleCount == 0)
        {
            report.OutputTriangles = 0;
            mesh.Positions = [];
            mesh.Indices = [];
            mesh.TriangleMaterials = [];
            mesh.CornerUVs = null;
            return report;
        }

        // 1. Weld within the vertex tolerance (spatial hash, so near-duplicates in different cells are found).
        var weldMap = WeldWithinTolerance(mesh.Positions, options.VertexTolerance, out var welded);
        report.WeldedVertices = welded;
        var positions = (double[])mesh.Positions.Clone();
        var indices = new int[mesh.Indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = weldMap[mesh.Indices[i]];
        }

        var alive = new bool[mesh.TriangleCount];
        for (var t = 0; t < alive.Length; t++)
        {
            var a = indices[3 * t];
            var b = indices[3 * t + 1];
            var c = indices[3 * t + 2];
            alive[t] = a != b && b != c && a != c;
            if (!alive[t])
            {
                report.DegenerateTriangles++;
            }
        }

        // 2. Collapse edges shorter than the minimum edge length. Only real triangle edges are merged, so two
        //    separate surfaces that merely lie close together are never fused.
        if (options.MinEdgeLength > 0)
        {
            var minSq = options.MinEdgeLength * options.MinEdgeLength;
            var uf = new UnionFind(n);
            var any = false;
            for (var t = 0; t < alive.Length; t++)
            {
                if (!alive[t])
                {
                    continue;
                }

                for (var e = 0; e < 3; e++)
                {
                    var a = indices[3 * t + e];
                    var b = indices[3 * t + (e + 1) % 3];
                    if (DistSq(positions, a, b) < minSq)
                    {
                        uf.Union(a, b);
                        any = true;
                    }
                }
            }

            if (any)
            {
                // Representative position = centroid of the cluster.
                var sum = new double[n * 3];
                var count = new int[n];
                for (var v = 0; v < n; v++)
                {
                    var r = uf.Find(v);
                    sum[3 * r] += positions[3 * v];
                    sum[3 * r + 1] += positions[3 * v + 1];
                    sum[3 * r + 2] += positions[3 * v + 2];
                    count[r]++;
                }

                for (var v = 0; v < n; v++)
                {
                    if (count[v] > 1)
                    {
                        positions[3 * v] = sum[3 * v] / count[v];
                        positions[3 * v + 1] = sum[3 * v + 1] / count[v];
                        positions[3 * v + 2] = sum[3 * v + 2] / count[v];
                    }
                }

                for (var i = 0; i < indices.Length; i++)
                {
                    indices[i] = uf.Find(indices[i]);
                }

                for (var t = 0; t < alive.Length; t++)
                {
                    if (!alive[t])
                    {
                        continue;
                    }

                    var a = indices[3 * t];
                    var b = indices[3 * t + 1];
                    var c = indices[3 * t + 2];
                    if (a == b || b == c || a == c)
                    {
                        alive[t] = false;
                        report.ShortEdgeTriangles++;
                    }
                }
            }

            // Moving cluster vertices to their centroid can create new short edges; drop what is left.
            for (var t = 0; t < alive.Length; t++)
            {
                if (!alive[t])
                {
                    continue;
                }

                var a = indices[3 * t];
                var b = indices[3 * t + 1];
                var c = indices[3 * t + 2];
                if (DistSq(positions, a, b) < minSq || DistSq(positions, b, c) < minSq || DistSq(positions, c, a) < minSq)
                {
                    alive[t] = false;
                    report.ShortEdgeTriangles++;
                }
            }
        }

        // 3. Zero-area triangles (collinear vertices) and exact duplicates (same three vertices, any order).
        var seen = new HashSet<(int, int, int)>();
        for (var t = 0; t < alive.Length; t++)
        {
            if (!alive[t])
            {
                continue;
            }

            var a = indices[3 * t];
            var b = indices[3 * t + 1];
            var c = indices[3 * t + 2];
            var pa = Get(positions, a);
            var ab = Get(positions, b) - pa;
            var ac = Get(positions, c) - pa;
            var cross = Vec3.Cross(ab, ac).LengthSquared;
            var maxEdgeSq = Math.Max(ab.LengthSquared, Math.Max(ac.LengthSquared, (Get(positions, c) - Get(positions, b)).LengthSquared));
            if (cross <= 1e-20 * maxEdgeSq * maxEdgeSq || cross == 0)
            {
                alive[t] = false;
                report.DegenerateTriangles++;
                continue;
            }

            if (!seen.Add(SortedKey(a, b, c)))
            {
                alive[t] = false;
                report.DuplicateTriangles++;
            }
        }

        Compact(mesh, positions, indices, alive);
        report.OutputTriangles = mesh.TriangleCount;
        return report;
    }

    /// <summary>Returns a map from each vertex to its representative; vertices within
    /// <paramref name="tolerance"/> of an earlier vertex map to that vertex.</summary>
    public static int[] WeldWithinTolerance(double[] positions, double tolerance, out long weldedCount)
    {
        var n = positions.Length / 3;
        var map = new int[n];
        weldedCount = 0;
        if (!(tolerance > 0))
        {
            for (var i = 0; i < n; i++)
            {
                map[i] = i;
            }

            return map;
        }

        var cell = tolerance;
        var tolSq = tolerance * tolerance;
        var grid = new Dictionary<(long, long, long), List<int>>(n);
        for (var i = 0; i < n; i++)
        {
            var x = positions[3 * i];
            var y = positions[3 * i + 1];
            var z = positions[3 * i + 2];
            var cx = (long)Math.Floor(x / cell);
            var cy = (long)Math.Floor(y / cell);
            var cz = (long)Math.Floor(z / cell);
            var found = -1;
            for (var dx = -1; dx <= 1 && found < 0; dx++)
            {
                for (var dy = -1; dy <= 1 && found < 0; dy++)
                {
                    for (var dz = -1; dz <= 1 && found < 0; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                        {
                            continue;
                        }

                        foreach (var j in list)
                        {
                            var ex = positions[3 * j] - x;
                            var ey = positions[3 * j + 1] - y;
                            var ez = positions[3 * j + 2] - z;
                            if (ex * ex + ey * ey + ez * ez <= tolSq)
                            {
                                found = j;
                                break;
                            }
                        }
                    }
                }
            }

            if (found >= 0)
            {
                map[i] = found;
                weldedCount++;
            }
            else
            {
                map[i] = i;
                var key = (cx, cy, cz);
                if (!grid.TryGetValue(key, out var list))
                {
                    list = new List<int>(1);
                    grid.Add(key, list);
                }

                list.Add(i);
            }
        }

        return map;
    }

    private static void Compact(ObjectMesh mesh, double[] positions, int[] indices, bool[] alive)
    {
        var aliveCount = 0;
        foreach (var a in alive)
        {
            if (a)
            {
                aliveCount++;
            }
        }

        var newIndex = new int[positions.Length / 3];
        Array.Fill(newIndex, -1);
        var newPositions = new List<double>();
        var newIndices = new int[aliveCount * 3];
        var newMats = new int[aliveCount];
        var newUvs = mesh.CornerUVs is not null ? new float[aliveCount * 6] : null;
        var k = 0;
        for (var t = 0; t < alive.Length; t++)
        {
            if (!alive[t])
            {
                continue;
            }

            for (var c = 0; c < 3; c++)
            {
                var v = indices[3 * t + c];
                if (newIndex[v] < 0)
                {
                    newIndex[v] = newPositions.Count / 3;
                    newPositions.Add(positions[3 * v]);
                    newPositions.Add(positions[3 * v + 1]);
                    newPositions.Add(positions[3 * v + 2]);
                }

                newIndices[3 * k + c] = newIndex[v];
            }

            newMats[k] = mesh.TriangleMaterials[t];
            if (newUvs is not null)
            {
                Array.Copy(mesh.CornerUVs!, 6 * t, newUvs, 6 * k, 6);
            }

            k++;
        }

        mesh.Positions = newPositions.ToArray();
        mesh.Indices = newIndices;
        mesh.TriangleMaterials = newMats;
        mesh.CornerUVs = newUvs;
    }

    private static (int, int, int) SortedKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static Vec3 Get(double[] p, int i) => new(p[3 * i], p[3 * i + 1], p[3 * i + 2]);

    private static double DistSq(double[] p, int a, int b)
    {
        var dx = p[3 * a] - p[3 * b];
        var dy = p[3 * a + 1] - p[3 * b + 1];
        var dz = p[3 * a + 2] - p[3 * b + 2];
        return dx * dx + dy * dy + dz * dz;
    }

    private sealed class UnionFind
    {
        private readonly int[] _parent;

        public UnionFind(int n)
        {
            _parent = new int[n];
            for (var i = 0; i < n; i++)
            {
                _parent[i] = i;
            }
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }

            return x;
        }

        public void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                // Keep the smaller index as root so results are deterministic.
                if (ra < rb)
                {
                    _parent[rb] = ra;
                }
                else
                {
                    _parent[ra] = rb;
                }
            }
        }
    }
}
