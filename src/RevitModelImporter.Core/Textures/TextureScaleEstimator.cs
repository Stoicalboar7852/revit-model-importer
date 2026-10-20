using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Processing;

namespace RevitModelImporter.Core.Textures;

/// <summary>Measures how many world units one UV unit spans on the actual mesh, per material.
/// Revit sizes a texture by its real-world width and height, so a tiling texture comes out at the right
/// scale when one texture repeat is set to this length.</summary>
public static class TextureScaleEstimator
{
    /// <summary>Area-weighted median of |∂P/∂u| and |∂P/∂v| for each material, in mesh units.
    /// Materials without usable UVs are absent from the result.</summary>
    public static Dictionary<int, (double SizeU, double SizeV)> Estimate(IEnumerable<ObjectMesh> meshes)
    {
        var samples = new Dictionary<int, List<(double U, double V, double W)>>();
        foreach (var mesh in meshes)
        {
            var uv = mesh.CornerUVs;
            if (uv is null)
            {
                continue;
            }

            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                var p0 = mesh.GetVertex(mesh.Indices[3 * t]);
                var p1 = mesh.GetVertex(mesh.Indices[3 * t + 1]);
                var p2 = mesh.GetVertex(mesh.Indices[3 * t + 2]);
                var o = 6 * t;
                double du1 = uv[o + 2] - uv[o], dv1 = uv[o + 3] - uv[o + 1];
                double du2 = uv[o + 4] - uv[o], dv2 = uv[o + 5] - uv[o + 1];
                var det = du1 * dv2 - du2 * dv1;
                if (Math.Abs(det) < 1e-12)
                {
                    continue;
                }

                var e1 = p1 - p0;
                var e2 = p2 - p0;
                var dPdu = (e1 * dv2 - e2 * dv1) / det;
                var dPdv = (e2 * du1 - e1 * du2) / det;
                var area = 0.5 * Vec3.Cross(e1, e2).Length;
                if (!(area > 0))
                {
                    continue;
                }

                var mat = mesh.TriangleMaterials[t];
                if (!samples.TryGetValue(mat, out var list))
                {
                    list = [];
                    samples.Add(mat, list);
                }

                list.Add((dPdu.Length, dPdv.Length, area));
            }
        }

        var result = new Dictionary<int, (double, double)>();
        foreach (var (mat, list) in samples)
        {
            var u = WeightedMedian(list.Select(s => (s.U, s.W)).ToList());
            var v = WeightedMedian(list.Select(s => (s.V, s.W)).ToList());
            if (u > 0 && v > 0 && double.IsFinite(u) && double.IsFinite(v))
            {
                result[mat] = (u, v);
            }
        }

        return result;
    }

    private static double WeightedMedian(List<(double Value, double Weight)> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        values.Sort((a, b) => a.Value.CompareTo(b.Value));
        var half = values.Sum(v => v.Weight) / 2;
        double acc = 0;
        foreach (var (value, weight) in values)
        {
            acc += weight;
            if (acc >= half)
            {
                return value;
            }
        }

        return values[^1].Value;
    }

    /// <summary>Fraction of UV coordinates outside [0, 1] (with a small margin). High values mean the
    /// texture is meant to tile; values near zero mean an atlas/unwrap.</summary>
    public static double FractionOutsideUnitSquare(IEnumerable<ObjectMesh> meshes)
    {
        long total = 0;
        long outside = 0;
        foreach (var mesh in meshes)
        {
            if (mesh.CornerUVs is not { } uv)
            {
                continue;
            }

            for (var i = 0; i < uv.Length; i++)
            {
                total++;
                if (uv[i] < -0.01f || uv[i] > 1.01f)
                {
                    outside++;
                }
            }
        }

        return total == 0 ? 0 : (double)outside / total;
    }
}
