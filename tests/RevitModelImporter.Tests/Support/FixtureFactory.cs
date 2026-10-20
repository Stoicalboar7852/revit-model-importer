using System.Numerics;
using Assimp;
using RevitModelImporter.Core.Scene;
using RevitModelImporter.Core.Textures;

namespace RevitModelImporter.Tests.Support;

/// <summary>
/// Builds test scenes in code and writes them with Assimp's own exporters, so no model files are downloaded or
/// committed. One instance per test run (xUnit collection fixture); files go to a temp folder.
/// </summary>
public sealed class FixtureFactory : IDisposable
{
    /// <summary>Export format id → file extension, for every exporter the fixtures use.</summary>
    public static readonly IReadOnlyList<(string FormatId, string FileName)> TexturedCubeExports =
    [
        ("obj", "cube.obj"),
        ("gltf2", "cube.gltf"),
        ("glb2", "cube.glb"),
        ("collada", "cube.dae"),
        ("fbx", "cube.fbx"),
        ("fbxa", "cube_ascii.fbx"),
        ("stl", "cube_ascii.stl"),
        ("stlb", "cube_binary.stl"),
        ("ply", "cube_ascii.ply"),
        ("plyb", "cube_binary.ply"),
        ("3ds", "cube.3ds"),
        ("3mf", "cube.3mf"),
    ];

    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public FixtureFactory()
    {
        Root = Path.Combine(Path.GetTempPath(), "rmi-fixtures-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        using var ctx = new AssimpContext();

        // Textured cube (external texture in a sibling textures/ folder; GLB/FBX get it embedded instead).
        var brick = Checker(64, 64, (178, 34, 34), (240, 240, 240), 8);
        var brickPng = PngWriter.Encode(brick);
        Directory.CreateDirectory(Path.Combine(Root, "textures"));
        File.WriteAllBytes(Path.Combine(Root, "textures", "brick.png"), brickPng);
        foreach (var (format, file) in TexturedCubeExports)
        {
            var embed = format is "glb2" or "fbx" or "fbxa";
            var scene = TexturedCubeScene(embed ? brickPng : null);
            if (format == "plyb")
            {
                // Assimp's binary PLY exporter declares RGBA vertex colours but writes UVs for meshes with
                // texture coordinates; reading that file back crashes Assimp (see the crash test below).
                scene.Meshes[0].TextureCoordinateChannels[0].Clear();
                scene.Meshes[0].UVComponentCount[0] = 0;
            }

            Export(ctx, scene, format, file);
        }

        // The malformed file described above: a real native crash in Assimp's reader on Linux.
        Export(ctx, TexturedCubeScene(null), "plyb", "crash_assimp_malformed.ply");

        // Several materials on one object (two meshes on one node, sharing an edge).
        Export(ctx, MultiMaterialScene(), "obj", "multimaterial.obj");
        Export(ctx, MultiMaterialScene(), "glb2", "multimaterial.glb");

        // Nested transforms + instancing (the same mesh referenced by two nodes).
        Export(ctx, NestedScene(), "gltf2", "nested.gltf");
        Export(ctx, NestedScene(), "collada", "nested.dae");

        // A tall Y-up box: 1 × 3 × 1 with Y up (glTF is always Y-up).
        Export(ctx, BoxScene(new Vector3(0, 0, 0), new Vector3(1, 3, 1)), "glb2", "tall_yup.glb");

        // A model in centimetres: 200 cm × 100 cm × 50 cm box with FBX UnitScaleFactor = 1 (cm per unit).
        var cm = BoxScene(new Vector3(0, 0, 0), new Vector3(200, 100, 50));
        cm.Metadata["UnitScaleFactor"] = new Metadata.Entry(MetaDataType.Double, 1.0);
        cm.Metadata["OriginalUnitScaleFactor"] = new Metadata.Entry(MetaDataType.Double, 1.0);
        Export(ctx, cm, "fbx", "centimeters.fbx");
        Export(ctx, BoxScene(new Vector3(0, 0, 0), new Vector3(200, 100, 50)), "obj", "centimeters.obj");

        // A 50 mm part written in metres (0.05 units). Read as millimetres it is 0.05 mm and trips
        // Revit's short-curve tolerance; read as metres every edge is longer than 1.6 mm, so nothing is lost.
        Export(ctx, SphereScene(0.025f, 12, 24), "obj", "tiny_part_in_meters.obj");

        // ~300k triangles: triggers the (strong) recommendation.
        Export(ctx, SphereScene(1f, 270, 560), "plyb", "sphere_300k.ply");

        // A missing texture (OBJ references a file that does not exist).
        Export(ctx, TexturedCubeScene(null, "missing_texture.png"), "obj", "missing_texture.obj");

        // Corrupted files.
        WriteCorrupt();
    }

    public string Root { get; }

    public string this[string name] => _files.TryGetValue(name, out var p) ? p : Path.Combine(Root, name);

    public bool Exists(string name) => _files.ContainsKey(name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Export(AssimpContext ctx, Assimp.Scene scene, string format, string fileName)
    {
        var path = Path.Combine(Root, fileName);
        if (!ctx.ExportFile(scene, path, format))
        {
            throw new InvalidOperationException($"Assimp could not export {fileName} as {format}.");
        }

        _files[fileName] = path;
    }

    private void WriteCorrupt()
    {
        // Random bytes with an FBX extension.
        var rng = new Random(42);
        var junk = new byte[4096];
        rng.NextBytes(junk);
        File.WriteAllBytes(Path.Combine(Root, "corrupt_random.fbx"), junk);
        _files["corrupt_random.fbx"] = Path.Combine(Root, "corrupt_random.fbx");

        // A valid GLB truncated in the middle of its binary chunk.
        var glb = File.ReadAllBytes(this["cube.glb"]);
        File.WriteAllBytes(Path.Combine(Root, "corrupt_truncated.glb"), glb[..(glb.Length / 3)]);
        _files["corrupt_truncated.glb"] = Path.Combine(Root, "corrupt_truncated.glb");

        // A 3DS file whose chunk lengths are garbage.
        var tds = File.ReadAllBytes(this["cube.3ds"]);
        for (var i = 6; i < tds.Length; i += 7)
        {
            tds[i] = 0xFF;
        }

        File.WriteAllBytes(Path.Combine(Root, "corrupt_chunks.3ds"), tds);
        _files["corrupt_chunks.3ds"] = Path.Combine(Root, "corrupt_chunks.3ds");

        // Empty file.
        File.WriteAllBytes(Path.Combine(Root, "empty.stl"), []);
        _files["empty.stl"] = Path.Combine(Root, "empty.stl");
    }

    // ---- Scenes ---------------------------------------------------------------------------------

    internal static RgbaImage Checker(int w, int h, (byte R, byte G, byte B) a, (byte R, byte G, byte B) b, int cell)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = ((x / cell) + (y / cell)) % 2 == 0 ? a : b;
                var i = 4 * (y * w + x);
                px[i] = c.R;
                px[i + 1] = c.G;
                px[i + 2] = c.B;
                px[i + 3] = 255;
            }
        }

        return new RgbaImage(w, h, px);
    }

    private static Assimp.Scene NewScene()
    {
        var scene = new Assimp.Scene { RootNode = new Node("Root") };
        return scene;
    }

    private static Material ColorMaterial(string name, float r, float g, float b)
    {
        var m = new Material { Name = name, ColorDiffuse = new Vector4(r, g, b, 1) };
        return m;
    }

    internal static Assimp.Scene TexturedCubeScene(byte[]? embeddedPng, string texturePath = "textures/brick.png")
    {
        var scene = NewScene();
        var material = ColorMaterial("Brick", 1, 1, 1);
        var path = texturePath;
        if (embeddedPng is not null)
        {
            scene.Textures.Add(new EmbeddedTexture("png", embeddedPng, "brick.png"));
            path = "*0";
        }

        material.TextureDiffuse = new TextureSlot(path, TextureType.Diffuse, 0, TextureMapping.FromUV, 0, 1f, TextureOperation.Multiply, TextureWrapMode.Wrap, TextureWrapMode.Wrap, 0);
        scene.Materials.Add(material);
        scene.Meshes.Add(Box("Cube", new Vector3(0, 0, 0), new Vector3(1, 1, 1), 0));
        var node = new Node("Cube", scene.RootNode);
        node.MeshIndices.Add(0);
        scene.RootNode.Children.Add(node);
        return scene;
    }

    private static Assimp.Scene MultiMaterialScene()
    {
        // Two boxes side by side on one node, sharing the face plane x = 1, red and blue.
        var scene = NewScene();
        scene.Materials.Add(ColorMaterial("Red", 0.8f, 0.1f, 0.1f));
        scene.Materials.Add(ColorMaterial("Blue", 0.1f, 0.2f, 0.8f));
        scene.Meshes.Add(Box("Left", new Vector3(0, 0, 0), new Vector3(1, 1, 1), 0));
        scene.Meshes.Add(Box("Right", new Vector3(1, 0, 0), new Vector3(2, 1, 1), 1));
        var node = new Node("TwoTone", scene.RootNode);
        node.MeshIndices.Add(0);
        node.MeshIndices.Add(1);
        scene.RootNode.Children.Add(node);
        return scene;
    }

    /// <summary>Parent at x = 10 rotated 90° about Y; child at local (0, 0, 5) scaled ×2 referencing the same
    /// unit cube as a second, untransformed instance at the root.</summary>
    private static Assimp.Scene NestedScene()
    {
        var scene = NewScene();
        scene.Materials.Add(ColorMaterial("Grey", 0.5f, 0.5f, 0.5f));
        scene.Meshes.Add(Box("Unit", new Vector3(0, 0, 0), new Vector3(1, 1, 1), 0));

        var parent = new Node("Parent", scene.RootNode)
        {
            Transform = ToAssimp(Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 0)),
        };
        var child = new Node("Child", parent)
        {
            Transform = ToAssimp(Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(0, 0, 5)),
        };
        child.MeshIndices.Add(0);
        parent.Children.Add(child);
        scene.RootNode.Children.Add(parent);

        var instance = new Node("Instance", scene.RootNode);
        instance.MeshIndices.Add(0);
        scene.RootNode.Children.Add(instance);
        return scene;
    }

    /// <summary>System.Numerics composes row-vector matrices; Assimp stores column-vector matrices
    /// (translation in M14/M24/M34), so transpose.</summary>
    private static Matrix4x4 ToAssimp(Matrix4x4 numerics) => Matrix4x4.Transpose(numerics);

    internal static Assimp.Scene BoxScene(Vector3 min, Vector3 max)
    {
        var scene = NewScene();
        scene.Materials.Add(ColorMaterial("Default", 0.7f, 0.7f, 0.7f));
        scene.Meshes.Add(Box("Box", min, max, 0));
        var node = new Node("Box", scene.RootNode);
        node.MeshIndices.Add(0);
        scene.RootNode.Children.Add(node);
        return scene;
    }

    internal static Assimp.Scene SphereScene(float radius, int rings, int segments)
    {
        var scene = NewScene();
        scene.Materials.Add(ColorMaterial("Sphere", 0.6f, 0.6f, 0.65f));
        var mesh = new Mesh("Sphere", PrimitiveType.Triangle) { MaterialIndex = 0 };
        for (var r = 0; r <= rings; r++)
        {
            var phi = MathF.PI * r / rings;
            for (var s = 0; s <= segments; s++)
            {
                var theta = 2 * MathF.PI * s / segments;
                mesh.Vertices.Add(new Vector3(radius * MathF.Sin(phi) * MathF.Cos(theta), radius * MathF.Cos(phi), radius * MathF.Sin(phi) * MathF.Sin(theta)));
                mesh.Normals.Add(Vector3.Normalize(mesh.Vertices[^1]));
            }
        }

        int V(int r, int s) => r * (segments + 1) + s;
        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                if (r != 0)
                {
                    mesh.Faces.Add(new Face([V(r, s), V(r, s + 1), V(r + 1, s)]));
                }

                if (r != rings - 1)
                {
                    mesh.Faces.Add(new Face([V(r + 1, s), V(r, s + 1), V(r + 1, s + 1)]));
                }
            }
        }

        scene.Meshes.Add(mesh);
        var node = new Node("Sphere", scene.RootNode);
        node.MeshIndices.Add(0);
        scene.RootNode.Children.Add(node);
        return scene;
    }

    /// <summary>Axis-aligned box with per-face vertices (24), UVs 0..1 per face, outward winding, 12 triangles.</summary>
    internal static Mesh Box(string name, Vector3 min, Vector3 max, int material)
    {
        var mesh = new Mesh(name, PrimitiveType.Triangle) { MaterialIndex = material };
        var uv = mesh.TextureCoordinateChannels[0];
        mesh.UVComponentCount[0] = 2;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            var i = mesh.Vertices.Count;
            mesh.Vertices.AddRange([a, b, c, d]);
            mesh.Normals.AddRange([n, n, n, n]);
            uv.AddRange([new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0)]);
            mesh.Faces.Add(new Face([i, i + 1, i + 2]));
            mesh.Faces.Add(new Face([i, i + 2, i + 3]));
        }

        var (x0, y0, z0, x1, y1, z1) = (min.X, min.Y, min.Z, max.X, max.Y, max.Z);
        Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), Vector3.UnitZ);
        Quad(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), -Vector3.UnitZ);
        Quad(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), Vector3.UnitX);
        Quad(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), -Vector3.UnitX);
        Quad(new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0), Vector3.UnitY);
        Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), -Vector3.UnitY);
        return mesh;
    }
}

[CollectionDefinition(Name)]
public sealed class FixtureCollection : ICollectionFixture<FixtureFactory>
{
    public const string Name = "Generated fixtures";
}
