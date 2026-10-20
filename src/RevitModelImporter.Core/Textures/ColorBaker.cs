using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Scene;

namespace RevitModelImporter.Core.Textures;

/// <summary>"Baked colors" mode: samples each textured triangle's colour from its texture (at the three corner
/// UVs and the centroid, multiplied by the material's base colour), reduces all sampled colours to a palette,
/// and points each triangle at its palette entry. Untextured materials keep their own flat colour.
/// Runs after simplification on the simplified triangles, using the UVs the simplifier carried along.</summary>
public static class ColorBaker
{
    public sealed record BakedMaterial(string Name, ColorRgba Color, bool IsPaletteEntry, int SourceMaterial);

    public sealed record Result(IReadOnlyList<BakedMaterial> Materials, int PaletteSize, long BakedTriangles);

    /// <param name="samplers">Diffuse texture sampler per source material, or null for untextured materials.</param>
    public static Result Bake(IReadOnlyList<ObjectMesh> meshes, IReadOnlyList<SceneMaterial> materials, IReadOnlyList<TextureSampler?> samplers, int paletteSize, string paletteNamePrefix)
    {
        var colors = new List<ColorRgba>();
        var weights = new List<double>();
        var owners = new List<(int Mesh, int Triangle)>();

        for (var mi = 0; mi < meshes.Count; mi++)
        {
            var mesh = meshes[mi];
            var uv = mesh.CornerUVs;
            if (uv is null)
            {
                continue;
            }

            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                var mat = mesh.TriangleMaterials[t];
                if (mat < 0 || mat >= samplers.Count || samplers[mat] is not { } sampler)
                {
                    continue;
                }

                var o = 6 * t;
                var c0 = sampler.Sample(uv[o], uv[o + 1]);
                var c1 = sampler.Sample(uv[o + 2], uv[o + 3]);
                var c2 = sampler.Sample(uv[o + 4], uv[o + 5]);
                var cc = sampler.Sample((uv[o] + uv[o + 2] + uv[o + 4]) / 3.0, (uv[o + 1] + uv[o + 3] + uv[o + 5]) / 3.0);
                var avg = new ColorRgba(
                    (c0.R + c1.R + c2.R + cc.R) / 4,
                    (c0.G + c1.G + c2.G + cc.G) / 4,
                    (c0.B + c1.B + c2.B + cc.B) / 4,
                    (c0.A + c1.A + c2.A + cc.A) / 4);
                colors.Add(avg.Multiply(materials[mat].BaseColor));
                weights.Add(TriangleArea(mesh, t));
                owners.Add((mi, t));
            }
        }

        // Output materials: every source material first (untextured ones are used as they are),
        // then the palette. Unused entries are removed later by MaterialCompactor.
        var output = new List<BakedMaterial>();
        for (var i = 0; i < materials.Count; i++)
        {
            output.Add(new BakedMaterial(materials[i].Name, materials[i].BaseColor, false, i));
        }

        if (colors.Count == 0)
        {
            return new Result(output, 0, 0);
        }

        var q = PaletteQuantizer.Quantize(colors, weights, paletteSize);
        var paletteBase = output.Count;
        for (var p = 0; p < q.Palette.Count; p++)
        {
            output.Add(new BakedMaterial($"{paletteNamePrefix} {q.Palette[p].ToHex()}", q.Palette[p], true, -1));
        }

        for (var i = 0; i < owners.Count; i++)
        {
            var (m, t) = owners[i];
            meshes[m].TriangleMaterials[t] = paletteBase + q.Assignment[i];
        }

        return new Result(output, q.Palette.Count, owners.Count);
    }

    private static double TriangleArea(ObjectMesh mesh, int t)
    {
        var a = mesh.GetVertex(mesh.Indices[3 * t]);
        var b = mesh.GetVertex(mesh.Indices[3 * t + 1]);
        var c = mesh.GetVertex(mesh.Indices[3 * t + 2]);
        return 0.5 * Vec3.Cross(b - a, c - a).Length;
    }
}
