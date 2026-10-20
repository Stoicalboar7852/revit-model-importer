// Quadric error metric edge-collapse simplification (Garland & Heckbert, "Surface Simplification Using
// Quadric Error Metrics", SIGGRAPH 1997).
//
// The iteration scheme (threshold schedule, per-triangle cached edge errors, dirty/deleted flags, reference
// lists rebuilt every few passes, flip test against the original face normal, barycentric UV update) follows
// Sven Forstmann's Fast-Quadric-Mesh-Simplification, https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification
// Copyright (c) 2014 Sven Forstmann, MIT License (full text in THIRD_PARTY_NOTICES.md).
//
// Changes in this C# version: double precision, scale normalisation (the threshold schedule is absolute),
// material seams and non-manifold edges treated like open borders, border-preserving constraint planes,
// constrained vertices may only collapse along a constrained edge onto an original vertex position,
// interior vertices may collapse onto constrained ones, a determinant guard for flat regions, and a final
// degenerate-triangle sweep.

using RevitModelImporter.Core.Processing;

namespace RevitModelImporter.Core.Simplification;

public sealed class QuadricSimplifier : IMeshSimplifier
{
    /// <summary>Weight of the planes that pin borders and seams, relative to face planes.</summary>
    public double BorderWeight { get; init; } = 1000.0;

    /// <summary>Exponent of the error threshold schedule (higher = faster, lower quality). 7 in the original.</summary>
    public double Aggressiveness { get; init; } = 7.0;

    public int MaxIterations { get; init; } = 150;

    public SimplifyResult Simplify(ObjectMesh mesh, int targetTriangles, CancellationToken cancellationToken = default)
    {
        var input = mesh.TriangleCount;
        if (targetTriangles >= input || input < 4)
        {
            return new SimplifyResult(input, input, 0, true);
        }

        var state = new State(mesh, BorderWeight);
        var iterations = state.Run(Math.Max(1, targetTriangles), Aggressiveness, MaxIterations, cancellationToken);
        state.WriteBack(mesh);
        var output = mesh.TriangleCount;
        return new SimplifyResult(input, output, iterations, output <= targetTriangles * 1.05 + 2);
    }

    private sealed class State
    {
        // Vertices
        private readonly double[] _p; // xyz, normalised coordinates
        private readonly double[] _q; // 10 per vertex
        private readonly int[] _tstart;
        private readonly int[] _tcount;
        private readonly bool[] _constrained;
        private readonly int _vertexCount;

        // Triangles (compacted every few iterations)
        private int[] _tv; // 3 per triangle
        private double[] _err; // 4 per triangle
        private bool[] _deleted;
        private bool[] _dirty;
        private double[] _n; // 3 per triangle, original normal
        private int[] _mat;
        private float[]? _uv; // 6 per triangle
        private int _triCount;

        // Vertex → triangle references
        private int[] _refT = [];
        private int[] _refV = [];
        private int _refCount;

        private readonly double _scale;
        private readonly double _cx, _cy, _cz;
        private readonly double _borderWeight;
        private readonly double _borderErrorLimit;

        // Scratch
        private bool[] _del0 = new bool[64];
        private bool[] _del1 = new bool[64];

        public State(ObjectMesh mesh, double borderWeight)
        {
            _borderWeight = borderWeight;
            // A constrained vertex may slide at most 1e-4 of the model diagonal off its border line.
            _borderErrorLimit = borderWeight * 1e-8;
            _vertexCount = mesh.VertexCount;
            var bounds = mesh.ComputeBounds();
            var c = bounds.Center;
            _cx = c.X;
            _cy = c.Y;
            _cz = c.Z;
            var diag = bounds.Diagonal;
            _scale = diag > 0 ? 1.0 / diag : 1.0;

            _p = new double[_vertexCount * 3];
            for (var i = 0; i < _vertexCount; i++)
            {
                _p[3 * i] = (mesh.Positions[3 * i] - _cx) * _scale;
                _p[3 * i + 1] = (mesh.Positions[3 * i + 1] - _cy) * _scale;
                _p[3 * i + 2] = (mesh.Positions[3 * i + 2] - _cz) * _scale;
            }

            _q = new double[_vertexCount * 10];
            _tstart = new int[_vertexCount];
            _tcount = new int[_vertexCount];
            _constrained = new bool[_vertexCount];

            _triCount = mesh.TriangleCount;
            _tv = (int[])mesh.Indices.Clone();
            _err = new double[_triCount * 4];
            _deleted = new bool[_triCount];
            _dirty = new bool[_triCount];
            _n = new double[_triCount * 3];
            _mat = (int[])mesh.TriangleMaterials.Clone();
            _uv = mesh.CornerUVs is null ? null : (float[])mesh.CornerUVs.Clone();
        }

        public int Run(int target, double aggressiveness, int maxIterations, CancellationToken ct)
        {
            var deletedTriangles = 0;
            var iteration = 0;
            for (; iteration < maxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();
                if (_triCount - deletedTriangles <= target)
                {
                    break;
                }

                if (iteration % 5 == 0)
                {
                    UpdateMesh(iteration);
                    deletedTriangles = 0;
                }

                Array.Clear(_dirty, 0, _triCount);
                var threshold = 1e-9 * Math.Pow(iteration + 3, aggressiveness);

                for (var i = 0; i < _triCount; i++)
                {
                    if (_err[4 * i + 3] > threshold || _deleted[i] || _dirty[i])
                    {
                        continue;
                    }

                    for (var j = 0; j < 3; j++)
                    {
                        if (_err[4 * i + j] >= threshold)
                        {
                            continue;
                        }

                        var i0 = _tv[3 * i + j];
                        var i1 = _tv[3 * i + (j + 1) % 3];

                        // Constrained (border/seam) vertices never move. An interior vertex may collapse onto a
                        // constrained one; two constrained vertices only along a constrained edge.
                        if (_constrained[i1] && !_constrained[i0])
                        {
                            (i0, i1) = (i1, i0);
                        }

                        if (_constrained[i0] && _constrained[i1] && !IsConstrainedEdge(i0, i1))
                        {
                            continue;
                        }

                        var bothConstrained = _constrained[i0] && _constrained[i1];
                        var error = CalculateError(i0, i1, out var px, out var py, out var pz);
                        if (bothConstrained)
                        {
                            // Moving along a border/seam is only allowed while it stays (nearly) straight,
                            // however high the threshold schedule has climbed.
                            if (error > _borderErrorLimit)
                            {
                                continue;
                            }

                            // Keep whichever endpoint is cheaper.
                            if (px == _p[3 * i1] && py == _p[3 * i1 + 1] && pz == _p[3 * i1 + 2])
                            {
                                (i0, i1) = (i1, i0);
                            }
                        }

                        EnsureScratch(_tcount[i0], _tcount[i1]);
                        if (Flipped(px, py, pz, i1, i0, _del0))
                        {
                            continue;
                        }

                        if (Flipped(px, py, pz, i0, i1, _del1))
                        {
                            continue;
                        }

                        if (_uv is not null)
                        {
                            UpdateUvs(i0, px, py, pz, _del0);
                            UpdateUvs(i1, px, py, pz, _del1);
                        }

                        _p[3 * i0] = px;
                        _p[3 * i0 + 1] = py;
                        _p[3 * i0 + 2] = pz;
                        for (var k = 0; k < 10; k++)
                        {
                            _q[10 * i0 + k] += _q[10 * i1 + k];
                        }

                        var tstart = _refCount;
                        UpdateTriangles(i0, i0, _del0, ref deletedTriangles);
                        UpdateTriangles(i0, i1, _del1, ref deletedTriangles);
                        var tcount = _refCount - tstart;
                        if (tcount <= _tcount[i0])
                        {
                            // Reuse the old slot to keep the reference list from growing.
                            if (tcount > 0)
                            {
                                Array.Copy(_refT, tstart, _refT, _tstart[i0], tcount);
                                Array.Copy(_refV, tstart, _refV, _tstart[i0], tcount);
                            }

                            _refCount = tstart;
                        }
                        else
                        {
                            _tstart[i0] = tstart;
                        }

                        _tcount[i0] = tcount;
                        _tcount[i1] = 0;
                        break;
                    }

                    if (_triCount - deletedTriangles <= target)
                    {
                        break;
                    }
                }
            }

            return iteration;
        }

        public void WriteBack(ObjectMesh mesh)
        {
            // Compact: drop deleted and (defensively) degenerate triangles, then unused vertices.
            var newIndex = new int[_vertexCount];
            Array.Fill(newIndex, -1);
            var positions = new List<double>();
            var indices = new List<int>(_triCount * 3);
            var mats = new List<int>(_triCount);
            var uvs = _uv is null ? null : new List<float>(_triCount * 6);
            var inv = 1.0 / _scale;
            for (var t = 0; t < _triCount; t++)
            {
                if (_deleted[t])
                {
                    continue;
                }

                var a = _tv[3 * t];
                var b = _tv[3 * t + 1];
                var c = _tv[3 * t + 2];
                if (a == b || b == c || a == c || TriangleAreaSq(a, b, c) <= 1e-30)
                {
                    continue;
                }

                foreach (var v in (ReadOnlySpan<int>)[a, b, c])
                {
                    if (newIndex[v] < 0)
                    {
                        newIndex[v] = positions.Count / 3;
                        positions.Add(_p[3 * v] * inv + _cx);
                        positions.Add(_p[3 * v + 1] * inv + _cy);
                        positions.Add(_p[3 * v + 2] * inv + _cz);
                    }

                    indices.Add(newIndex[v]);
                }

                mats.Add(_mat[t]);
                if (uvs is not null)
                {
                    for (var k = 0; k < 6; k++)
                    {
                        uvs.Add(_uv![6 * t + k]);
                    }
                }
            }

            mesh.Positions = positions.ToArray();
            mesh.Indices = indices.ToArray();
            mesh.TriangleMaterials = mats.ToArray();
            mesh.CornerUVs = uvs?.ToArray();
        }

        private double TriangleAreaSq(int a, int b, int c)
        {
            var abx = _p[3 * b] - _p[3 * a];
            var aby = _p[3 * b + 1] - _p[3 * a + 1];
            var abz = _p[3 * b + 2] - _p[3 * a + 2];
            var acx = _p[3 * c] - _p[3 * a];
            var acy = _p[3 * c + 1] - _p[3 * a + 1];
            var acz = _p[3 * c + 2] - _p[3 * a + 2];
            var nx = aby * acz - abz * acy;
            var ny = abz * acx - abx * acz;
            var nz = abx * acy - aby * acx;
            return nx * nx + ny * ny + nz * nz;
        }

        private void EnsureScratch(int n0, int n1)
        {
            if (_del0.Length < n0)
            {
                _del0 = new bool[Math.Max(n0, _del0.Length * 2)];
            }

            if (_del1.Length < n1)
            {
                _del1 = new bool[Math.Max(n1, _del1.Length * 2)];
            }
        }

        /// <summary>True when the edge i0–i1 is an open border, a material seam or non-manifold.</summary>
        private bool IsConstrainedEdge(int i0, int i1)
        {
            var count = 0;
            var firstMat = int.MinValue;
            var start = _tstart[i0];
            var end = start + _tcount[i0];
            for (var k = start; k < end; k++)
            {
                var t = _refT[k];
                if (_deleted[t])
                {
                    continue;
                }

                if (_tv[3 * t] == i1 || _tv[3 * t + 1] == i1 || _tv[3 * t + 2] == i1)
                {
                    count++;
                    if (firstMat == int.MinValue)
                    {
                        firstMat = _mat[t];
                    }
                    else if (_mat[t] != firstMat)
                    {
                        return true;
                    }
                }
            }

            return count == 1 || count > 2;
        }

        /// <summary>Checks whether moving <paramref name="vertex"/> to p flips or degenerates any of its
        /// triangles that do not also contain <paramref name="other"/> (those disappear and are flagged in
        /// <paramref name="deleted"/>).</summary>
        private bool Flipped(double px, double py, double pz, int other, int vertex, bool[] deleted)
        {
            var start = _tstart[vertex];
            var count = _tcount[vertex];
            for (var k = 0; k < count; k++)
            {
                var t = _refT[start + k];
                if (_deleted[t])
                {
                    continue;
                }

                var s = _refV[start + k];
                var id1 = _tv[3 * t + (s + 1) % 3];
                var id2 = _tv[3 * t + (s + 2) % 3];
                if (id1 == other || id2 == other)
                {
                    deleted[k] = true;
                    continue;
                }

                var d1x = _p[3 * id1] - px;
                var d1y = _p[3 * id1 + 1] - py;
                var d1z = _p[3 * id1 + 2] - pz;
                var d2x = _p[3 * id2] - px;
                var d2y = _p[3 * id2 + 1] - py;
                var d2z = _p[3 * id2 + 2] - pz;
                var l1 = Math.Sqrt(d1x * d1x + d1y * d1y + d1z * d1z);
                var l2 = Math.Sqrt(d2x * d2x + d2y * d2y + d2z * d2z);
                if (l1 <= 0 || l2 <= 0)
                {
                    return true;
                }

                d1x /= l1; d1y /= l1; d1z /= l1;
                d2x /= l2; d2y /= l2; d2z /= l2;
                if (Math.Abs(d1x * d2x + d1y * d2y + d1z * d2z) > 0.999)
                {
                    return true;
                }

                var nx = d1y * d2z - d1z * d2y;
                var ny = d1z * d2x - d1x * d2z;
                var nz = d1x * d2y - d1y * d2x;
                var nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl <= 0)
                {
                    return true;
                }

                deleted[k] = false;
                // Triangle corners are ordered (s, s+1, s+2) with p at s, so (d1, d2) keeps the winding.
                var dot = (nx * _n[3 * t] + ny * _n[3 * t + 1] + nz * _n[3 * t + 2]) / nl;
                if (dot < 0.2)
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateUvs(int vertex, double px, double py, double pz, bool[] deleted)
        {
            var start = _tstart[vertex];
            var count = _tcount[vertex];
            for (var k = 0; k < count; k++)
            {
                var t = _refT[start + k];
                if (_deleted[t] || deleted[k])
                {
                    continue;
                }

                var s = _refV[start + k];
                Interpolate(t, px, py, pz, out var u, out var v);
                _uv![6 * t + 2 * s] = u;
                _uv[6 * t + 2 * s + 1] = v;
            }
        }

        /// <summary>UV at point p using the barycentric coordinates of p in the plane of triangle t.</summary>
        private void Interpolate(int t, double px, double py, double pz, out float u, out float v)
        {
            var a = _tv[3 * t];
            var b = _tv[3 * t + 1];
            var c = _tv[3 * t + 2];
            double v0x = _p[3 * b] - _p[3 * a], v0y = _p[3 * b + 1] - _p[3 * a + 1], v0z = _p[3 * b + 2] - _p[3 * a + 2];
            double v1x = _p[3 * c] - _p[3 * a], v1y = _p[3 * c + 1] - _p[3 * a + 1], v1z = _p[3 * c + 2] - _p[3 * a + 2];
            double v2x = px - _p[3 * a], v2y = py - _p[3 * a + 1], v2z = pz - _p[3 * a + 2];
            var d00 = v0x * v0x + v0y * v0y + v0z * v0z;
            var d01 = v0x * v1x + v0y * v1y + v0z * v1z;
            var d11 = v1x * v1x + v1y * v1y + v1z * v1z;
            var d20 = v2x * v0x + v2y * v0y + v2z * v0z;
            var d21 = v2x * v1x + v2y * v1y + v2z * v1z;
            var denom = d00 * d11 - d01 * d01;
            double wb, wc;
            if (Math.Abs(denom) < 1e-300)
            {
                wb = 0;
                wc = 0;
            }
            else
            {
                wb = (d11 * d20 - d01 * d21) / denom;
                wc = (d00 * d21 - d01 * d20) / denom;
            }

            // Not clamped to the triangle: p is usually just outside triangle t (it moves onto a neighbouring
            // vertex), and extrapolating the triangle's linear UV field is exact for planar mappings. Extreme
            // weights (slivers) are limited so a bad fit cannot throw the UV far across the texture.
            wb = Math.Clamp(wb, -10.0, 11.0);
            wc = Math.Clamp(wc, -10.0, 11.0);
            var wa = 1.0 - wb - wc;
            var o = 6 * t;
            u = (float)(wa * _uv![o] + wb * _uv[o + 2] + wc * _uv[o + 4]);
            v = (float)(wa * _uv[o + 1] + wb * _uv[o + 3] + wc * _uv[o + 5]);
        }

        private void UpdateTriangles(int i0, int vertex, bool[] deleted, ref int deletedTriangles)
        {
            var start = _tstart[vertex];
            var count = _tcount[vertex];
            for (var k = 0; k < count; k++)
            {
                var t = _refT[start + k];
                var s = _refV[start + k];
                if (_deleted[t])
                {
                    continue;
                }

                if (deleted[k])
                {
                    _deleted[t] = true;
                    deletedTriangles++;
                    continue;
                }

                _tv[3 * t + s] = i0;
                _dirty[t] = true;
                var e0 = CalculateError(_tv[3 * t], _tv[3 * t + 1], out _, out _, out _);
                var e1 = CalculateError(_tv[3 * t + 1], _tv[3 * t + 2], out _, out _, out _);
                var e2 = CalculateError(_tv[3 * t + 2], _tv[3 * t], out _, out _, out _);
                _err[4 * t] = e0;
                _err[4 * t + 1] = e1;
                _err[4 * t + 2] = e2;
                _err[4 * t + 3] = Math.Min(e0, Math.Min(e1, e2));
                AddRef(t, s);
            }
        }

        private void AddRef(int t, int s)
        {
            if (_refCount == _refT.Length)
            {
                var n = Math.Max(16, _refT.Length * 2);
                Array.Resize(ref _refT, n);
                Array.Resize(ref _refV, n);
            }

            _refT[_refCount] = t;
            _refV[_refCount] = s;
            _refCount++;
        }

        private void UpdateMesh(int iteration)
        {
            if (iteration > 0)
            {
                CompactTriangles();
            }

            // Build vertex → triangle references.
            Array.Clear(_tcount, 0, _vertexCount);
            for (var t = 0; t < _triCount; t++)
            {
                _tcount[_tv[3 * t]]++;
                _tcount[_tv[3 * t + 1]]++;
                _tcount[_tv[3 * t + 2]]++;
            }

            var tstart = 0;
            for (var v = 0; v < _vertexCount; v++)
            {
                _tstart[v] = tstart;
                tstart += _tcount[v];
                _tcount[v] = 0;
            }

            if (_refT.Length < _triCount * 3)
            {
                _refT = new int[_triCount * 3];
                _refV = new int[_triCount * 3];
            }

            _refCount = _triCount * 3;
            for (var t = 0; t < _triCount; t++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var v = _tv[3 * t + j];
                    var slot = _tstart[v] + _tcount[v];
                    _refT[slot] = t;
                    _refV[slot] = j;
                    _tcount[v]++;
                }
            }

            if (iteration != 0)
            {
                return;
            }

            // Face planes → vertex quadrics, original normals for the flip test.
            Array.Clear(_q);
            for (var t = 0; t < _triCount; t++)
            {
                var a = _tv[3 * t];
                var b = _tv[3 * t + 1];
                var c = _tv[3 * t + 2];
                double abx = _p[3 * b] - _p[3 * a], aby = _p[3 * b + 1] - _p[3 * a + 1], abz = _p[3 * b + 2] - _p[3 * a + 2];
                double acx = _p[3 * c] - _p[3 * a], acy = _p[3 * c + 1] - _p[3 * a + 1], acz = _p[3 * c + 2] - _p[3 * a + 2];
                var nx = aby * acz - abz * acy;
                var ny = abz * acx - abx * acz;
                var nz = abx * acy - aby * acx;
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0)
                {
                    nx /= len;
                    ny /= len;
                    nz /= len;
                }

                _n[3 * t] = nx;
                _n[3 * t + 1] = ny;
                _n[3 * t + 2] = nz;
                var d = -(nx * _p[3 * a] + ny * _p[3 * a + 1] + nz * _p[3 * a + 2]);
                AddPlane(a, nx, ny, nz, d, 1.0);
                AddPlane(b, nx, ny, nz, d, 1.0);
                AddPlane(c, nx, ny, nz, d, 1.0);
            }

            MarkConstraints();

            for (var t = 0; t < _triCount; t++)
            {
                var e0 = CalculateError(_tv[3 * t], _tv[3 * t + 1], out _, out _, out _);
                var e1 = CalculateError(_tv[3 * t + 1], _tv[3 * t + 2], out _, out _, out _);
                var e2 = CalculateError(_tv[3 * t + 2], _tv[3 * t], out _, out _, out _);
                _err[4 * t] = e0;
                _err[4 * t + 1] = e1;
                _err[4 * t + 2] = e2;
                _err[4 * t + 3] = Math.Min(e0, Math.Min(e1, e2));
            }
        }

        /// <summary>Finds border, seam and non-manifold edges, constrains their vertices and adds a plane through
        /// each such edge, perpendicular to the adjacent face, so collapsing along a curved border is expensive.</summary>
        private void MarkConstraints()
        {
            Array.Clear(_constrained);
            var neighbours = new List<(int Vertex, int Count, int Material, bool Mixed, int Triangle)>();
            for (var v = 0; v < _vertexCount; v++)
            {
                neighbours.Clear();
                var start = _tstart[v];
                var end = start + _tcount[v];
                for (var k = start; k < end; k++)
                {
                    var t = _refT[k];
                    for (var j = 0; j < 3; j++)
                    {
                        var id = _tv[3 * t + j];
                        if (id == v)
                        {
                            continue;
                        }

                        var found = -1;
                        for (var m = 0; m < neighbours.Count; m++)
                        {
                            if (neighbours[m].Vertex == id)
                            {
                                found = m;
                                break;
                            }
                        }

                        if (found < 0)
                        {
                            neighbours.Add((id, 1, _mat[t], false, t));
                        }
                        else
                        {
                            var e = neighbours[found];
                            neighbours[found] = (e.Vertex, e.Count + 1, e.Material, e.Mixed || e.Material != _mat[t], e.Triangle);
                        }
                    }
                }

                foreach (var e in neighbours)
                {
                    if (e.Count == 1 || e.Count > 2 || e.Mixed)
                    {
                        _constrained[v] = true;
                        _constrained[e.Vertex] = true;
                        if (v < e.Vertex)
                        {
                            AddEdgeConstraint(v, e.Vertex, e.Triangle);
                        }
                    }
                }
            }
        }

        private void AddEdgeConstraint(int a, int b, int t)
        {
            double ex = _p[3 * b] - _p[3 * a], ey = _p[3 * b + 1] - _p[3 * a + 1], ez = _p[3 * b + 2] - _p[3 * a + 2];
            // Plane normal = edge × face normal (perpendicular to the face, containing the edge).
            var fnx = _n[3 * t];
            var fny = _n[3 * t + 1];
            var fnz = _n[3 * t + 2];
            var nx = ey * fnz - ez * fny;
            var ny = ez * fnx - ex * fnz;
            var nz = ex * fny - ey * fnx;
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len <= 0)
            {
                return;
            }

            nx /= len;
            ny /= len;
            nz /= len;
            var d = -(nx * _p[3 * a] + ny * _p[3 * a + 1] + nz * _p[3 * a + 2]);
            AddPlane(a, nx, ny, nz, d, _borderWeight);
            AddPlane(b, nx, ny, nz, d, _borderWeight);
        }

        private void AddPlane(int v, double a, double b, double c, double d, double w)
        {
            var o = 10 * v;
            _q[o] += w * a * a;
            _q[o + 1] += w * a * b;
            _q[o + 2] += w * a * c;
            _q[o + 3] += w * a * d;
            _q[o + 4] += w * b * b;
            _q[o + 5] += w * b * c;
            _q[o + 6] += w * b * d;
            _q[o + 7] += w * c * c;
            _q[o + 8] += w * c * d;
            _q[o + 9] += w * d * d;
        }

        private void CompactTriangles()
        {
            var dst = 0;
            for (var t = 0; t < _triCount; t++)
            {
                if (_deleted[t])
                {
                    continue;
                }

                if (dst != t)
                {
                    _tv[3 * dst] = _tv[3 * t];
                    _tv[3 * dst + 1] = _tv[3 * t + 1];
                    _tv[3 * dst + 2] = _tv[3 * t + 2];
                    Array.Copy(_err, 4 * t, _err, 4 * dst, 4);
                    Array.Copy(_n, 3 * t, _n, 3 * dst, 3);
                    _mat[dst] = _mat[t];
                    if (_uv is not null)
                    {
                        Array.Copy(_uv, 6 * t, _uv, 6 * dst, 6);
                    }
                }

                _deleted[dst] = false;
                dst++;
            }

            _triCount = dst;
        }

        /// <summary>Quadric error of collapsing edge v1–v2, and the position that minimises it.</summary>
        private double CalculateError(int v1, int v2, out double px, out double py, out double pz)
        {
            var o1 = 10 * v1;
            var o2 = 10 * v2;
            var q0 = _q[o1] + _q[o2];
            var q1 = _q[o1 + 1] + _q[o2 + 1];
            var q2 = _q[o1 + 2] + _q[o2 + 2];
            var q3 = _q[o1 + 3] + _q[o2 + 3];
            var q4 = _q[o1 + 4] + _q[o2 + 4];
            var q5 = _q[o1 + 5] + _q[o2 + 5];
            var q6 = _q[o1 + 6] + _q[o2 + 6];
            var q7 = _q[o1 + 7] + _q[o2 + 7];
            var q8 = _q[o1 + 8] + _q[o2 + 8];
            var q9 = _q[o1 + 9] + _q[o2 + 9];

            var c1 = _constrained[v1];
            var c2 = _constrained[v2];

            if (!c1 && !c2)
            {
                // det of [[q0 q1 q2][q1 q4 q5][q2 q5 q7]]
                var det = Det(q0, q1, q2, q1, q4, q5, q2, q5, q7);
                if (Math.Abs(det) > 1e-15)
                {
                    px = -1.0 / det * Det(q1, q2, q3, q4, q5, q6, q5, q7, q8);
                    py = 1.0 / det * Det(q0, q2, q3, q1, q5, q6, q2, q7, q8);
                    pz = -1.0 / det * Det(q0, q1, q3, q1, q4, q6, q2, q5, q8);

                    // Reject optimal points far away from the edge (near-singular quadrics in flat regions).
                    var mx = 0.5 * (_p[3 * v1] + _p[3 * v2]);
                    var my = 0.5 * (_p[3 * v1 + 1] + _p[3 * v2 + 1]);
                    var mz = 0.5 * (_p[3 * v1 + 2] + _p[3 * v2 + 2]);
                    var ex = _p[3 * v1] - _p[3 * v2];
                    var ey = _p[3 * v1 + 1] - _p[3 * v2 + 1];
                    var ez = _p[3 * v1 + 2] - _p[3 * v2 + 2];
                    var edgeSq = ex * ex + ey * ey + ez * ez;
                    var dx = px - mx;
                    var dy = py - my;
                    var dz = pz - mz;
                    if (dx * dx + dy * dy + dz * dz <= 4 * edgeSq)
                    {
                        return VertexError(q0, q1, q2, q3, q4, q5, q6, q7, q8, q9, px, py, pz);
                    }
                }
            }

            // Candidates: the endpoints (and the midpoint when neither endpoint is constrained).
            // v1 is the survivor in the main loop, so when only v2 is constrained the caller swapped them.
            double x1 = _p[3 * v1], y1 = _p[3 * v1 + 1], z1 = _p[3 * v1 + 2];
            double x2 = _p[3 * v2], y2 = _p[3 * v2 + 1], z2 = _p[3 * v2 + 2];
            var e1 = VertexError(q0, q1, q2, q3, q4, q5, q6, q7, q8, q9, x1, y1, z1);
            if (c1)
            {
                px = x1;
                py = y1;
                pz = z1;
                if (c2)
                {
                    // Both constrained: the cheaper endpoint survives (the caller swaps when it is v2).
                    var e2c = VertexError(q0, q1, q2, q3, q4, q5, q6, q7, q8, q9, x2, y2, z2);
                    if (e2c < e1)
                    {
                        px = x2;
                        py = y2;
                        pz = z2;
                        return e2c;
                    }
                }

                return e1;
            }

            if (c2)
            {
                px = x2;
                py = y2;
                pz = z2;
                return VertexError(q0, q1, q2, q3, q4, q5, q6, q7, q8, q9, x2, y2, z2);
            }

            var e2 = VertexError(q0, q1, q2, q3, q4, q5, q6, q7, q8, q9, x2, y2, z2);
            double x3 = 0.5 * (x1 + x2), y3 = 0.5 * (y1 + y2), z3 = 0.5 * (z1 + z2);
            var e3 = VertexError(q0, q1, q2, q3, q4, q5, q6, q7, q8, q9, x3, y3, z3);
            var min = Math.Min(e1, Math.Min(e2, e3));
            if (min == e1)
            {
                px = x1; py = y1; pz = z1;
            }
            else if (min == e2)
            {
                px = x2; py = y2; pz = z2;
            }
            else
            {
                px = x3; py = y3; pz = z3;
            }

            return min;
        }

        private static double Det(double a11, double a12, double a13, double a21, double a22, double a23, double a31, double a32, double a33) =>
            a11 * a22 * a33 + a13 * a21 * a32 + a12 * a23 * a31 - a13 * a22 * a31 - a11 * a23 * a32 - a12 * a21 * a33;

        private static double VertexError(double q0, double q1, double q2, double q3, double q4, double q5, double q6, double q7, double q8, double q9, double x, double y, double z) =>
            q0 * x * x + 2 * q1 * x * y + 2 * q2 * x * z + 2 * q3 * x + q4 * y * y + 2 * q5 * y * z + 2 * q6 * y + q7 * z * z + 2 * q8 * z + q9;
    }
}
