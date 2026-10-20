using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Scene;
using RevitModelImporter.Core.Statistics;
using RevitModelImporter.Core.Textures;
using RevitModelImporter.Core.WorkerProtocol;

namespace RevitModelImporter.Core.Processing;

/// <summary>Produces the statistics shown in the Import dialog from a loaded scene.</summary>
public static class ModelAnalyzer
{
    /// <summary>Above this fraction of UV coordinates outside 0–1 the texture is treated as tiling.</summary>
    public const double TilingUvFraction = 0.10;

    public static AnalysisResult Analyze(SceneModel scene)
    {
        var objects = SceneFlattener.Flatten(scene, Mat4.Identity);
        var result = new AnalysisResult
        {
            FilePath = scene.SourcePath,
            FormatId = scene.FormatId,
            FileSizeBytes = File.Exists(scene.SourcePath) ? new FileInfo(scene.SourcePath).Length : 0,
            ObjectCount = objects.Count,
            MeshCount = scene.Meshes.Count(m => m.TriangleCount > 0),
            TriangleCount = objects.Sum(o => (long)o.TriangleCount),
            SourcePolygonCount = scene.SourcePolygonCount,
            VertexCount = objects.Sum(o => (long)o.VertexCount),
            MaterialCount = objects.SelectMany(o => o.TriangleMaterials).Distinct().Count(),
            DetectedUnit = scene.DetectedUnit,
            UnitSource = scene.UnitSource,
            DefaultUpAxis = scene.DefaultUpAxis,
            UpAxisSource = scene.UpAxisSource,
            ObjectTriangleCounts = objects.Select(o => o.TriangleCount).ToList(),
        };

        var bounds = new BoundingBox();
        foreach (var o in objects)
        {
            bounds.Add(o.ComputeBounds());
            result.ShortestEdges.AddMesh(o);
        }

        if (!bounds.IsEmpty)
        {
            result.BoundsMin = [bounds.Min.X, bounds.Min.Y, bounds.Min.Z];
            result.BoundsMax = [bounds.Max.X, bounds.Max.Y, bounds.Max.Z];
        }

        // Textures actually referenced by materials that triangles use.
        var usedMaterials = new HashSet<int>(objects.SelectMany(o => o.TriangleMaterials));
        var usedTextures = new HashSet<int>();
        var texturedMaterials = new HashSet<int>();
        foreach (var mi in usedMaterials)
        {
            if (mi < 0 || mi >= scene.Materials.Count)
            {
                continue;
            }

            var m = scene.Materials[mi];
            foreach (var t in new[] { m.BaseColorTexture, m.NormalTexture, m.BumpTexture })
            {
                if (t is not null && t.TextureIndex >= 0 && t.TextureIndex < scene.Textures.Count)
                {
                    usedTextures.Add(t.TextureIndex);
                }
            }

            if (m.BaseColorTexture is { } bt && bt.TextureIndex >= 0 && bt.TextureIndex < scene.Textures.Count && !scene.Textures[bt.TextureIndex].IsMissing)
            {
                texturedMaterials.Add(mi);
            }
        }

        result.TextureCount = usedTextures.Count;
        result.TexturesEmbedded = usedTextures.Count(i => scene.Textures[i].IsEmbedded);
        result.TexturesFound = usedTextures.Count(i => !scene.Textures[i].IsMissing);
        result.MissingTextures = usedTextures
            .Where(i => scene.Textures[i].IsMissing)
            .Select(i => scene.Textures[i].OriginalReference)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.HasTextures = usedTextures.Count > 0;
        result.HasTexCoords = objects.Any(o => o.CornerUVs is not null);

        var texturedObjects = objects
            .Where(o => o.CornerUVs is not null && o.TriangleMaterials.Any(texturedMaterials.Contains))
            .ToList();
        result.UvOutsideUnitFraction = TextureScaleEstimator.FractionOutsideUnitSquare(texturedObjects);
        (result.RecommendedMaterialMode, result.MaterialModeReason) = RecommendMode(result, texturedMaterials.Count > 0, texturedObjects.Count > 0);
        result.Warnings.AddRange(scene.Warnings);
        if (result.TriangleCount == 0)
        {
            result.Warnings.Add("The file contains no surface geometry (triangles or polygons) that can be imported.");
        }

        return result;
    }

    private static (MaterialMode Mode, string Reason) RecommendMode(AnalysisResult r, bool anyTexturedMaterial, bool texturedWithUvs)
    {
        if (!r.HasTextures)
        {
            return (MaterialMode.FlatColors, "The model has no textures.");
        }

        if (!anyTexturedMaterial)
        {
            return (MaterialMode.FlatColors, "None of the model's textures could be found.");
        }

        if (!texturedWithUvs)
        {
            return (MaterialMode.FlatColors, "The model has textures but no texture coordinates.");
        }

        return r.UvOutsideUnitFraction > TilingUvFraction
            ? (MaterialMode.Textured, "The texture coordinates repeat, which suggests tiling textures (brick, wood, tiles).")
            : (MaterialMode.BakedColors, "The texture coordinates stay inside one image, which suggests an unwrapped texture or atlas (scans, game assets).");
    }
}
