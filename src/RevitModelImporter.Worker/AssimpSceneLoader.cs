using System.IO.Compression;
using System.Numerics;
using System.Xml;
using Assimp;
using Assimp.Unmanaged;
using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Scene;
using RevitModelImporter.Core.Textures;
using RevitModelImporter.Core.Units;

namespace RevitModelImporter.Worker;

/// <summary>Thrown when Assimp cannot read a file (corrupt, truncated, or not the format it claims).</summary>
public sealed class ModelLoadException : Exception
{
    public ModelLoadException(string message)
        : base(message)
    {
    }

    public ModelLoadException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Loads any Assimp-supported file into the format-neutral <see cref="SceneModel"/>.</summary>
public sealed class AssimpSceneLoader
{
    private const PostProcessSteps Steps =
        PostProcessSteps.Triangulate
        | PostProcessSteps.SortByPrimitiveType
        | PostProcessSteps.JoinIdenticalVertices
        | PostProcessSteps.FindDegenerates
        | PostProcessSteps.FindInvalidData
        | PostProcessSteps.GenerateUVCoords
        | PostProcessSteps.TransformUVCoords
        | PostProcessSteps.ValidateDataStructure;

    public static string AssimpVersion
    {
        get
        {
            var lib = AssimpLibrary.Instance;
            return $"{lib.GetVersionMajor()}.{lib.GetVersionMinor()}.{lib.GetVersionRevision():x}";
        }
    }

    public static IReadOnlyList<(string Extension, string Description)> SupportedFormats()
    {
        using var ctx = new AssimpContext();
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in ctx.GetImporterDescriptions())
        {
            foreach (var e in d.FileExtensions)
            {
                descriptions.TryAdd(e.TrimStart('.'), CleanDescription(d.Name));
            }
        }

        return ctx.GetSupportedImportFormats()
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0 && !e.Contains('.', StringComparison.Ordinal) && !FormatDefaults.ExcludedExtensions.Contains(e))
            .Distinct()
            .OrderBy(e => e, StringComparer.Ordinal)
            .Select(e => (e, descriptions.GetValueOrDefault(e, e.ToUpperInvariant())))
            .ToList();
    }

    private static string CleanDescription(string name) =>
        name.Replace(" Importer", "", StringComparison.Ordinal).Replace(" Reader", "", StringComparison.Ordinal).Trim();

    public SceneModel Load(string path, IReadOnlyList<string> textureSearchFolders, Action<string, double, string?>? progress = null)
    {
        var lib = AssimpLibrary.Instance;
        var formatId = FormatDefaults.NormalizeExtension(path);
        var store = lib.CreatePropertyStore();
        Assimp.Scene managed;
        long polygonCount;
        try
        {
            // Drop points and lines entirely; keep FBX free of $AssimpFbx$ pivot helper nodes; let
            // FindDegenerates remove (not convert) degenerate faces; keep FBX in its own units (we read
            // UnitScaleFactor) and its own up axis (we offer the choice).
            lib.SetImportPropertyInteger(store, AiConfigs.AI_CONFIG_PP_SBP_REMOVE, (int)(PrimitiveType.Point | PrimitiveType.Line));
            lib.SetImportPropertyInteger(store, AiConfigs.AI_CONFIG_IMPORT_FBX_PRESERVE_PIVOTS, 0);
            lib.SetImportPropertyInteger(store, AiConfigs.AI_CONFIG_PP_FD_REMOVE, 1);
            lib.SetImportPropertyInteger(store, AiConfigs.AI_CONFIG_FBX_CONVERT_TO_M, 0);
            lib.SetImportPropertyInteger(store, AiConfigs.AI_CONFIG_IMPORT_FBX_READ_ANIMATIONS, 0);
            lib.SetImportPropertyInteger(store, AiConfigs.AI_CONFIG_IMPORT_FBX_READ_CAMERAS, 0);
            lib.SetImportPropertyInteger(store, AiConfigs.AI_CONFIG_IMPORT_FBX_READ_LIGHTS, 0);

            progress?.Invoke("Loading", 0.05, "Reading " + Path.GetFileName(path));
            var ptr = lib.ImportFile(path, PostProcessSteps.None, IntPtr.Zero, store);
            if (ptr == IntPtr.Zero)
            {
                throw new ModelLoadException(Clean(lib.GetErrorString()));
            }

            try
            {
                // Count polygons as stored in the file, before triangulation.
                polygonCount = CountFaces(ptr);
                progress?.Invoke("Loading", 0.15, "Triangulating and validating");
                var processed = lib.ApplyPostProcessing(ptr, Steps);
                if (processed == IntPtr.Zero)
                {
                    // ApplyPostProcessing releases the scene when it fails.
                    ptr = IntPtr.Zero;
                    throw new ModelLoadException(Clean(lib.GetErrorString()));
                }

                ptr = processed;
                progress?.Invoke("Loading", 0.20, "Reading scene data");
                managed = Assimp.Scene.FromUnmanagedScene(ptr) ?? throw new ModelLoadException("The file contains no scene.");
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                {
                    lib.ReleaseImport(ptr);
                }
            }
        }
        finally
        {
            lib.ReleasePropertyStore(store);
        }

        var scene = Convert(managed, path, formatId, textureSearchFolders);
        scene.SourcePolygonCount = polygonCount;
        return scene;
    }

    private static string Clean(string? assimpError) =>
        string.IsNullOrWhiteSpace(assimpError) ? "Assimp could not read the file." : assimpError.Trim();

    private static long CountFaces(IntPtr scenePtr)
    {
        var scene = MemoryHelper.MarshalStructure<AiScene>(scenePtr);
        long faces = 0;
        for (var i = 0; i < scene.NumMeshes; i++)
        {
            var meshPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(scene.Meshes, i * IntPtr.Size);
            var mesh = MemoryHelper.MarshalStructure<AiMesh>(meshPtr);
            if ((mesh.PrimitiveTypes & (PrimitiveType.Triangle | PrimitiveType.Polygon)) != 0)
            {
                faces += mesh.NumFaces;
            }
        }

        return faces;
    }

    internal static SceneModel Convert(Assimp.Scene src, string path, string formatId, IReadOnlyList<string> textureSearchFolders)
    {
        var scene = new SceneModel { SourcePath = Path.GetFullPath(path), FormatId = formatId };
        var resolver = new TextureResolver(path, textureSearchFolders);
        var textureIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var isPbrFormat = formatId is "gltf" or "glb" or "vrm";

        // Materials
        foreach (var m in src.Materials)
        {
            scene.Materials.Add(ConvertMaterial(m, src, scene, resolver, textureIndex, isPbrFormat));
        }

        if (scene.Materials.Count == 0)
        {
            scene.Materials.Add(new SceneMaterial { Name = "Default" });
        }

        // Meshes (triangles only; points/lines were removed by SortByPrimitiveType)
        var skippedNonTriangle = 0;
        foreach (var m in src.Meshes)
        {
            var mesh = new SceneMesh
            {
                Name = m.Name ?? "",
                MaterialIndex = m.MaterialIndex >= 0 && m.MaterialIndex < scene.Materials.Count ? m.MaterialIndex : 0,
            };
            var verts = m.Vertices;
            var pos = new double[verts.Count * 3];
            for (var i = 0; i < verts.Count; i++)
            {
                pos[3 * i] = verts[i].X;
                pos[3 * i + 1] = verts[i].Y;
                pos[3 * i + 2] = verts[i].Z;
            }

            mesh.Positions = pos;
            var uvChannel = scene.Materials[mesh.MaterialIndex].BaseColorTexture?.UvChannel ?? 0;
            if (!m.HasTextureCoords(uvChannel))
            {
                uvChannel = 0;
            }

            if (m.HasTextureCoords(uvChannel))
            {
                var tc = m.TextureCoordinateChannels[uvChannel];
                var uv = new float[verts.Count * 2];
                for (var i = 0; i < Math.Min(tc.Count, verts.Count); i++)
                {
                    uv[2 * i] = tc[i].X;
                    uv[2 * i + 1] = tc[i].Y;
                }

                mesh.TexCoords = uv;
            }

            var indices = new List<int>(m.FaceCount * 3);
            foreach (var f in m.Faces)
            {
                if (f.IndexCount != 3)
                {
                    skippedNonTriangle++;
                    continue;
                }

                var a = f.Indices[0];
                var b = f.Indices[1];
                var c = f.Indices[2];
                if ((uint)a >= verts.Count || (uint)b >= verts.Count || (uint)c >= verts.Count)
                {
                    continue;
                }

                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
            }

            mesh.Indices = indices.ToArray();
            scene.Meshes.Add(mesh);
        }

        if (skippedNonTriangle > 0)
        {
            scene.Warnings.Add($"{skippedNonTriangle:N0} faces that are not triangles after triangulation (points or lines) were skipped.");
        }

        scene.Root = ConvertNode(src.RootNode);
        DetectUnitsAndAxes(src, scene, path, formatId);
        return scene;
    }

    private static SceneNode ConvertNode(Node? n)
    {
        var node = new SceneNode { Name = n?.Name ?? "Root" };
        if (n is null)
        {
            return node;
        }

        var t = n.Transform;
        node.LocalTransform = new Mat4(
            t.M11, t.M12, t.M13, t.M14,
            t.M21, t.M22, t.M23, t.M24,
            t.M31, t.M32, t.M33, t.M34,
            t.M41, t.M42, t.M43, t.M44);
        node.MeshIndices.AddRange(n.MeshIndices);
        foreach (var c in n.Children)
        {
            node.Children.Add(ConvertNode(c));
        }

        return node;
    }

    private static SceneMaterial ConvertMaterial(Material m, Assimp.Scene src, SceneModel scene, TextureResolver resolver, Dictionary<string, int> textureIndex, bool isPbrFormat)
    {
        var mat = new SceneMaterial { Name = m.HasName ? m.Name : "" };

        // Base colour: glTF/PBR "$clr.base" is linear; classic diffuse colours (OBJ Kd, FBX, 3DS…) are
        // authored as display (sRGB) values.
        var baseColor = m.GetNonTextureProperty("$clr.base");
        if (baseColor is not null && baseColor.GetFloatArrayValue() is { Length: >= 3 } bc)
        {
            mat.BaseColor = new ColorRgba(bc[0], bc[1], bc[2], bc.Length >= 4 ? bc[3] : 1.0);
            mat.IsPbr = true;
        }
        else if (m.HasColorDiffuse)
        {
            var d = m.ColorDiffuse;
            mat.BaseColor = isPbrFormat
                ? new ColorRgba(d.X, d.Y, d.Z, d.W)
                : ColorRgba.FromSrgb(Math.Clamp(d.X, 0, 1), Math.Clamp(d.Y, 0, 1), Math.Clamp(d.Z, 0, 1), d.W);
        }

        var opacity = mat.BaseColor.A;
        if (m.HasOpacity)
        {
            opacity = Math.Min(opacity, m.Opacity);
        }

        if (m.HasTransparencyFactor && !isPbrFormat && m.TransparencyFactor > 0 && m.TransparencyFactor <= 1)
        {
            // Some exporters write "Tr"/transparency instead of "d"/opacity.
            opacity = Math.Min(opacity, 1.0 - m.TransparencyFactor);
        }

        if (!(opacity > 0.01))
        {
            // A fully transparent material is almost always an exporter bug; keep it visible.
            opacity = 1.0;
        }

        mat.BaseColor = mat.BaseColor with { A = Math.Clamp(opacity, 0, 1) };

        if (m.GetNonTextureProperty("$mat.metallicFactor") is { } metal)
        {
            mat.Metallic = metal.GetFloatValue();
        }

        if (m.GetNonTextureProperty("$mat.roughnessFactor") is { } rough)
        {
            mat.Roughness = rough.GetFloatValue();
        }
        else if (m.HasShininess && m.Shininess > 0)
        {
            // Phong exponent → approximate roughness.
            mat.Roughness = Math.Clamp(Math.Sqrt(2.0 / (m.Shininess + 2.0)), 0.05, 1.0);
        }

        mat.TwoSided = m.HasTwoSided && m.IsTwoSided;

        mat.BaseColorTexture = TextureFor(m, TextureType.BaseColor, src, scene, resolver, textureIndex)
            ?? TextureFor(m, TextureType.Diffuse, src, scene, resolver, textureIndex);
        mat.NormalTexture = TextureFor(m, TextureType.Normals, src, scene, resolver, textureIndex)
            ?? TextureFor(m, TextureType.NormalCamera, src, scene, resolver, textureIndex);
        mat.BumpTexture = TextureFor(m, TextureType.Height, src, scene, resolver, textureIndex)
            ?? TextureFor(m, TextureType.Displacement, src, scene, resolver, textureIndex);
        return mat;
    }

    private static TextureRef? TextureFor(Material m, TextureType type, Assimp.Scene src, SceneModel scene, TextureResolver resolver, Dictionary<string, int> textureIndex)
    {
        if (m.GetMaterialTextureCount(type) == 0 || !m.GetMaterialTexture(type, 0, out var slot) || string.IsNullOrWhiteSpace(slot.FilePath))
        {
            return null;
        }

        var reference = slot.FilePath;
        if (!textureIndex.TryGetValue(reference, out var index))
        {
            index = scene.Textures.Count;
            scene.Textures.Add(LoadTexture(reference, src, resolver));
            textureIndex.Add(reference, index);
        }

        return new TextureRef(index, Math.Max(0, slot.UVIndex));
    }

    private static SceneTexture LoadTexture(string reference, Assimp.Scene src, TextureResolver resolver)
    {
        var tex = new SceneTexture { OriginalReference = reference };
        var embedded = src.GetEmbeddedTexture(reference);
        if (embedded is not null)
        {
            if (embedded.IsCompressed && embedded.HasCompressedData)
            {
                tex.EmbeddedData = embedded.CompressedData;
                tex.EmbeddedFormatHint = embedded.CompressedFormatHint;
            }
            else if (embedded.HasNonCompressedData && embedded.Width > 0 && embedded.Height > 0)
            {
                var texels = embedded.NonCompressedData;
                var px = new byte[embedded.Width * embedded.Height * 4];
                for (var i = 0; i < Math.Min(texels.Length, embedded.Width * embedded.Height); i++)
                {
                    px[4 * i] = texels[i].R;
                    px[4 * i + 1] = texels[i].G;
                    px[4 * i + 2] = texels[i].B;
                    px[4 * i + 3] = texels[i].A;
                }

                tex.EmbeddedPixels = new RgbaImage(embedded.Width, embedded.Height, px);
            }

            if (!string.IsNullOrEmpty(embedded.Filename) && reference.StartsWith('*'))
            {
                tex.OriginalReference = reference + " (" + Path.GetFileName(embedded.Filename) + ")";
            }

            if (tex.IsEmbedded)
            {
                return tex;
            }
        }

        tex.ResolvedPath = resolver.Resolve(reference);
        return tex;
    }

    private static void DetectUnitsAndAxes(Assimp.Scene src, SceneModel scene, string path, string formatId)
    {
        scene.DefaultUpAxis = FormatDefaults.DefaultUpAxis(formatId);
        scene.UpAxisSource = $"{formatId.ToUpperInvariant()} convention";

        switch (formatId)
        {
            case "gltf" or "glb" or "vrm":
                scene.DetectedUnit = LengthUnit.Meters;
                scene.UnitSource = "glTF is always in meters";
                scene.UpAxisSource = "glTF is always Y-up";
                break;
            case "dae" or "zae":
                // Assimp applies <unit meter=…> and <up_axis> to the root node, so the scene is in metres, Y-up.
                scene.DetectedUnit = LengthUnit.Meters;
                scene.UnitSource = "Collada <unit> (applied by the reader)";
                scene.DefaultUpAxis = UpAxis.YUp;
                scene.UpAxisSource = "Collada <up_axis> (applied by the reader)";
                break;
            case "fbx":
                if (TryGetMetadataDouble(src, "UnitScaleFactor", out var cmPerUnit) && cmPerUnit > 0)
                {
                    scene.DetectedUnit = LengthUnits.FromMetersPerUnit(cmPerUnit / 100.0);
                    scene.UnitSource = scene.DetectedUnit is null
                        ? null
                        : $"FBX UnitScaleFactor = {cmPerUnit.ToString(System.Globalization.CultureInfo.InvariantCulture)} cm per unit";
                    if (scene.DetectedUnit is null)
                    {
                        scene.Warnings.Add($"The FBX file uses an unusual unit ({cmPerUnit} cm per unit); please choose the source unit.");
                    }
                }

                if (TryGetMetadataDouble(src, "UpAxis", out var up))
                {
                    scene.DefaultUpAxis = (int)up == 2 ? UpAxis.ZUp : UpAxis.YUp;
                    scene.UpAxisSource = "FBX UpAxis setting";
                }

                break;
            case "3mf":
                scene.DetectedUnit = Read3mfUnit(path) ?? LengthUnit.Millimeters;
                scene.UnitSource = "3MF model unit";
                break;
        }
    }

    private static bool TryGetMetadataDouble(Assimp.Scene src, string key, out double value)
    {
        value = 0;
        if (src.Metadata is null || !src.Metadata.TryGetValue(key, out var entry) || entry.Data is null)
        {
            return false;
        }

        switch (entry.Data)
        {
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case ulong ul:
                value = ul;
                return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads the unit attribute of the 3MF model part (default millimetres per the 3MF spec).</summary>
    internal static LengthUnit? Read3mfUnit(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "model")
                {
                    return reader.GetAttribute("unit") switch
                    {
                        "micron" => null,
                        "millimeter" or null => LengthUnit.Millimeters,
                        "centimeter" => LengthUnit.Centimeters,
                        "meter" => LengthUnit.Meters,
                        "inch" => LengthUnit.Inches,
                        "foot" => LengthUnit.Feet,
                        _ => null,
                    };
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    internal static Vector3 ToVector(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
