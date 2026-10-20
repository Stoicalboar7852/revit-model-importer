using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RevitModelImporter.Core.Intermediate;
using RevitModelImporter.Core.Scene;
using RevitModelImporter.Core.Simplification;
using RevitModelImporter.Core.Textures;
using RevitModelImporter.Core.Units;

namespace RevitModelImporter.Core.Processing;

public sealed class ConversionOptions
{
    public LengthUnit Unit { get; init; } = LengthUnit.Meters;

    public UpAxis UpAxis { get; init; } = UpAxis.YUp;

    public MaterialMode MaterialMode { get; init; } = MaterialMode.FlatColors;

    public SimplifyTarget? Simplify { get; init; }

    public int PaletteSize { get; init; } = 32;

    /// <summary>Where textures for Revit are written. Null: textures are not exported (Textured mode then
    /// degrades to flat colours with a warning).</summary>
    public string? TextureDirectory { get; init; }

    public CleanupOptions Cleanup { get; init; } = CleanupOptions.RevitDefaults;

    public bool Merge { get; init; }

    public int MinimumTrianglesPerMesh { get; init; } = TargetAllocator.DefaultMinimumPerMesh;

    /// <summary>Warn that units are probably wrong when more than this fraction of triangles is lost to
    /// Revit's short-curve tolerance.</summary>
    public double ShortEdgeWarningFraction { get; init; } = 0.05;

    /// <summary>Textures are downscaled to at most this size for colour sampling (baking and averages).</summary>
    public int SamplingTextureSize { get; init; } = 1024;
}

public interface IConversionProgress
{
    void Report(string stage, double fraction, string? message = null);

    void Warn(string message);
}

/// <summary>Turns a loaded scene into Revit-ready meshes and materials: flatten to world space, convert units
/// and axes, simplify, resolve materials and textures, bake colours, clean up for Revit's tolerances.</summary>
public sealed class ConversionPipeline
{
    private readonly ITextureDecoder _decoder;
    private readonly IMeshSimplifier _simplifier;

    public ConversionPipeline(ITextureDecoder decoder, IMeshSimplifier? simplifier = null)
    {
        _decoder = decoder;
        _simplifier = simplifier ?? new QuadricSimplifier();
    }

    public (IntermediateHeader Header, List<ObjectMesh> Meshes) Run(SceneModel scene, ConversionOptions options, IConversionProgress progress, CancellationToken ct)
    {
        var stats = new ConversionStats();
        var header = new IntermediateHeader
        {
            MaterialMode = options.MaterialMode,
            Stats = stats,
            Source = new SourceInfo
            {
                Path = scene.SourcePath,
                FormatId = scene.FormatId,
                SizeBytes = File.Exists(scene.SourcePath) ? new FileInfo(scene.SourcePath).Length : 0,
                LastWriteUtc = File.Exists(scene.SourcePath) ? File.GetLastWriteTimeUtc(scene.SourcePath) : default,
            },
        };
        var warnings = header.Warnings;
        void Warn(string w)
        {
            warnings.Add(w);
            progress.Warn(w);
        }

        var sw = Stopwatch.StartNew();

        // 1. World space, Revit axes, feet.
        progress.Report("Flattening", 0.30, "Applying node transforms, axes and units");
        var meshes = SceneFlattener.Flatten(scene, AxisConversion.SourceToRevit(options.UpAxis, options.Unit));
        stats.SourceTriangles = meshes.Sum(m => (long)m.TriangleCount);
        stats.TrianglesBeforeSimplify = stats.SourceTriangles;
        Lap(stats, "flatten", sw);

        // 2. Simplify (per object, target spread proportionally with a floor per object).
        if (options.Simplify is { } target && stats.SourceTriangles > 0)
        {
            var totalTarget = target.ResolveTotal(stats.SourceTriangles);
            var counts = meshes.Select(m => m.TriangleCount).ToList();
            var targets = TargetAllocator.Allocate(counts, totalTarget, options.MinimumTrianglesPerMesh);
            var done = 0;
            var order = Enumerable.Range(0, meshes.Count).OrderByDescending(i => counts[i]).ToArray();
            Parallel.ForEach(
                order,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
                i =>
                {
                    if (targets[i] < meshes[i].TriangleCount)
                    {
                        _simplifier.Simplify(meshes[i], targets[i], ct);
                    }

                    var n = Interlocked.Increment(ref done);
                    progress.Report("Simplifying", 0.30 + 0.40 * n / meshes.Count, $"Simplified {n} of {meshes.Count} objects");
                });
            stats.Simplified = true;
        }

        stats.TrianglesAfterSimplify = meshes.Sum(m => (long)m.TriangleCount);
        Lap(stats, "simplify", sw);
        ct.ThrowIfCancellationRequested();

        // 3. Materials and textures.
        progress.Report("Materials", 0.72, "Preparing materials and textures");
        var store = new TextureStore(scene, _decoder, options.TextureDirectory, options.SamplingTextureSize);
        var modelName = ModelName(scene);
        var materials = BuildMaterials(scene, meshes, options, store, modelName, stats, Warn);
        stats.TexturesWritten = store.Written;
        stats.MissingTextures = store.Missing.ToList();
        stats.UnsupportedTextures = store.Unsupported.ToList();
        stats.TextureFolder = store.Written > 0 ? options.TextureDirectory : null;
        foreach (var u in store.Unsupported)
        {
            Warn($"Texture '{u}' is in a format that cannot be converted (for example WebP or KTX2); its material uses a flat color instead.");
        }

        Lap(stats, "materials", sw);
        ct.ThrowIfCancellationRequested();

        // 4. Clean up for Revit's tolerances.
        progress.Report("Cleaning up", 0.85, "Welding vertices and removing slivers Revit would reject");
        var sizeBeforeCleanup = new Geometry.BoundingBox();
        foreach (var m in meshes)
        {
            sizeBeforeCleanup.Add(m.ComputeBounds());
        }

        var cleanup = new CleanupReport();
        foreach (var m in meshes)
        {
            ct.ThrowIfCancellationRequested();
            cleanup.Add(MeshCleanup.Clean(m, options.Cleanup));
        }

        stats.WeldedVertices = cleanup.WeldedVertices;
        stats.DegenerateTrianglesRemoved = cleanup.DegenerateTriangles;
        stats.DuplicateTrianglesRemoved = cleanup.DuplicateTriangles;
        stats.ShortEdgeTrianglesRemoved = cleanup.ShortEdgeTriangles;
        var beforeCleanup = Math.Max(1, cleanup.InputTriangles);
        var shortFraction = (double)cleanup.ShortEdgeTriangles / beforeCleanup;
        if (cleanup.ShortEdgeTriangles > 0)
        {
            var minEdgeMm = options.Cleanup.MinEdgeLength * LengthUnits.MetersPerFoot * 1000.0;
            var msg = string.Create(CultureInfo.InvariantCulture, $"{cleanup.ShortEdgeTriangles:N0} of {cleanup.InputTriangles:N0} triangles ({shortFraction:P1}) had edges shorter than Revit's minimum of {minEdgeMm:0.##} mm and were merged away.");
            if (shortFraction > options.ShortEdgeWarningFraction)
            {
                msg += $" That is a lot: the source units are probably wrong (the model is {Describe(sizeBeforeCleanup)} when read as {options.Unit.DisplayName().ToLowerInvariant()}). Try a larger unit.";
            }

            Warn(msg);
        }

        var emptyObjects = meshes.Count(m => m.TriangleCount == 0);
        meshes.RemoveAll(m => m.TriangleCount == 0);
        if (emptyObjects > 0)
        {
            Warn($"{emptyObjects} object(s) had no triangles left after cleanup and were skipped.");
        }

        Lap(stats, "cleanup", sw);

        // 5. Optional merge into a single element.
        if (options.Merge && meshes.Count > 1)
        {
            meshes = [ObjectMesh.Merge(modelName, meshes)];
        }

        // 6. Drop unused materials, count usage.
        var kept = MaterialCompactor.Compact(meshes, materials.Count);
        header.Materials = kept.Select(i => materials[i]).ToList();
        foreach (var m in meshes)
        {
            foreach (var t in m.TriangleMaterials)
            {
                header.Materials[t].TriangleCount++;
            }
        }

        stats.OutputTriangles = meshes.Sum(m => (long)m.TriangleCount);
        stats.OutputVertices = meshes.Sum(m => (long)m.VertexCount);
        if (stats.OutputTriangles == 0 && stats.SourceTriangles > 0)
        {
            Warn("Nothing is left to import after cleanup. The model is far too small at the chosen units; pick a larger unit.");
        }

        return (header, meshes);
    }

    private static List<IntermediateMaterial> BuildMaterials(SceneModel scene, List<ObjectMesh> meshes, ConversionOptions options, TextureStore store, string modelName, ConversionStats stats, Action<string> warn)
    {
        var sourceMaterials = scene.Materials.Count > 0 ? scene.Materials : [new SceneMaterial { Name = "Default" }];
        var result = new List<IntermediateMaterial>();

        // Average texture colour per material (used by Flat mode, by the shaded-view colour in Textured mode,
        // and for textured materials on triangles without UVs in Baked mode).
        ColorRgba EffectiveFlatColor(SceneMaterial m)
        {
            if (m.BaseColorTexture is { } t && store.GetSampler(t.TextureIndex) is { } s)
            {
                return s.Average().Multiply(m.BaseColor) with { A = m.BaseColor.A };
            }

            return m.BaseColor;
        }

        switch (options.MaterialMode)
        {
            case MaterialMode.Textured:
            {
                if (options.TextureDirectory is null)
                {
                    warn("No texture folder was given, so textures are not attached; materials use flat colors.");
                }

                var scales = TextureScaleEstimator.Estimate(meshes);
                for (var i = 0; i < sourceMaterials.Count; i++)
                {
                    var m = sourceMaterials[i];
                    var im = FromSource(m, EffectiveFlatColor(m), modelName, i);
                    if (m.BaseColorTexture is { } dt && store.Export(dt.TextureIndex) is { } diffusePath)
                    {
                        im.DiffuseTexture = diffusePath;
                        if (scales.TryGetValue(i, out var size))
                        {
                            im.TextureSizeX = size.SizeU;
                            im.TextureSizeY = size.SizeV;
                        }
                        else
                        {
                            // No usable UVs: one metre per repeat is a neutral default.
                            im.TextureSizeX = im.TextureSizeY = 1.0 / LengthUnits.MetersPerFoot;
                        }
                    }

                    var bump = m.BumpTexture ?? m.NormalTexture;
                    if (bump is not null && store.Export(bump.TextureIndex) is { } bumpPath)
                    {
                        im.BumpTexture = bumpPath;
                        im.BumpAmount = m.BumpTexture is not null ? 1.0 : 0.3;
                    }

                    im.Key = Signature("textured", im.Name, im.Color, im.Opacity, Path.GetFileName(im.DiffuseTexture), Round(im.TextureSizeX), Round(im.TextureSizeY), Path.GetFileName(im.BumpTexture));
                    result.Add(im);
                }

                break;
            }

            case MaterialMode.BakedColors:
            {
                var samplers = sourceMaterials.Select(m => m.BaseColorTexture is { } t ? store.GetSampler(t.TextureIndex) : null).ToList();
                var bake = ColorBaker.Bake(meshes, sourceMaterials, samplers, options.PaletteSize, modelName + " baked");
                stats.PaletteSize = bake.PaletteSize;
                foreach (var b in bake.Materials)
                {
                    if (b.IsPaletteEntry)
                    {
                        var (r, g, bl) = b.Color.ToSrgbBytes();
                        var im = new IntermediateMaterial
                        {
                            Name = b.Name,
                            Kind = MaterialKind.Baked,
                            Color = [r, g, bl],
                            Opacity = Math.Clamp(b.Color.A, 0, 1),
                            Glossiness = 0.1,
                        };
                        im.Key = Signature("baked", im.Color, Round(im.Opacity));
                        result.Add(im);
                    }
                    else
                    {
                        var m = sourceMaterials[b.SourceMaterial];
                        var im = FromSource(m, EffectiveFlatColor(m), modelName, b.SourceMaterial);
                        im.Key = Signature("flat", im.Name, im.Color, im.Opacity);
                        result.Add(im);
                    }
                }

                if (bake.BakedTriangles == 0 && sourceMaterials.Any(m => m.BaseColorTexture is not null))
                {
                    warn("No triangle could be baked (textures missing or no texture coordinates); flat colors are used instead.");
                }

                break;
            }

            default:
            {
                for (var i = 0; i < sourceMaterials.Count; i++)
                {
                    var m = sourceMaterials[i];
                    var im = FromSource(m, EffectiveFlatColor(m), modelName, i);
                    im.Key = Signature("flat", im.Name, im.Color, im.Opacity);
                    result.Add(im);
                }

                break;
            }
        }

        return result;
    }

    private static IntermediateMaterial FromSource(SceneMaterial m, ColorRgba color, string modelName, int index)
    {
        var (r, g, b) = color.ToSrgbBytes();
        var name = string.IsNullOrWhiteSpace(m.Name) ? $"Material {index + 1}" : m.Name.Trim();
        return new IntermediateMaterial
        {
            Name = $"{modelName} - {name}",
            Kind = MaterialKind.Source,
            Color = [r, g, b],
            Opacity = Math.Clamp(m.BaseColor.A, 0, 1),
            IsMetal = m.Metallic >= 0.5,
            Glossiness = Math.Clamp(1.0 - m.Roughness, 0, 1),
        };
    }

    private static string ModelName(SceneModel scene)
    {
        var name = Path.GetFileNameWithoutExtension(scene.SourcePath);
        return string.IsNullOrWhiteSpace(name) ? "Model" : name.Trim();
    }

    private static double Round(double v) => Math.Round(v, 4);

    internal static string Signature(params object?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            sb.Append(p switch
            {
                byte[] bytes => Convert.ToHexString(bytes),
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                null => "-",
                _ => Convert.ToString(p, CultureInfo.InvariantCulture),
            });
            sb.Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24];
    }

    private static string Describe(Geometry.BoundingBox box)
    {
        var s = box.Size * LengthUnits.MetersPerFoot;
        return string.Create(CultureInfo.InvariantCulture, $"only {s.X * 1000:0.##} × {s.Y * 1000:0.##} × {s.Z * 1000:0.##} mm");
    }

    private static void Lap(ConversionStats stats, string stage, Stopwatch sw)
    {
        stats.StageMilliseconds[stage] = sw.ElapsedMilliseconds;
        sw.Restart();
    }
}
