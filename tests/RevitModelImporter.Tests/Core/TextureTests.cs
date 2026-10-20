using RevitModelImporter.Core.Geometry;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Scene;
using RevitModelImporter.Core.Textures;
using RevitModelImporter.Tests.Support;
using RevitModelImporter.Worker;

namespace RevitModelImporter.Tests.Core;

public sealed class TextureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rmi-tex-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void PngWriter_RoundTripsThroughAnIndependentDecoder()
    {
        var image = FixtureFactory.Checker(37, 19, (10, 200, 30), (250, 5, 128), 4);
        var png = PngWriter.Encode(image);

        Assert.Equal("png", ImageFormats.Sniff(png));
        var decoded = new ImageDecoder().TryDecode(png, "x.png", out var error);
        Assert.Null(error);
        Assert.NotNull(decoded);
        Assert.Equal(37, decoded!.Width);
        Assert.Equal(19, decoded.Height);
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Decoder_ConvertsTgaAndRejectsWebp()
    {
        // Uncompressed 2×1 BGR TGA.
        byte[] tga = [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 24, 0x20, 0, 0, 255, 0, 255, 0];
        var img = new ImageDecoder().TryDecode(tga, "a.tga", out _);
        Assert.NotNull(img);
        Assert.Equal([255, 0, 0, 255], img!.Pixels[..4]);
        Assert.Equal([0, 255, 0, 255], img.Pixels[4..8]);

        byte[] webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8 "u8];
        Assert.Equal("webp", ImageFormats.Sniff(webp));
        Assert.Null(new ImageDecoder().TryDecode(webp, "a.webp", out var error));
        Assert.Contains("not supported", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Downscale_AveragesInLinearSpace()
    {
        var image = FixtureFactory.Checker(4096, 2048, (0, 0, 0), (255, 255, 255), 1);
        var small = ImageFormats.Downscale(image, 1024);
        Assert.Equal(1024, small.Width);
        Assert.Equal(512, small.Height);
        // Mean of black and white in linear light is 0.5 → sRGB ≈ 188, not 128.
        Assert.InRange(small.Pixels[0], 185, 190);
    }

    [Fact]
    public void Resolver_FindsTexturesTheWayModelsAreShipped()
    {
        var model = Path.Combine(_dir, "model", "chair.obj");
        Directory.CreateDirectory(Path.Combine(_dir, "model", "Textures"));
        Directory.CreateDirectory(Path.Combine(_dir, "model", "sub"));
        Directory.CreateDirectory(Path.Combine(_dir, "elsewhere"));
        File.WriteAllText(model, "");
        File.WriteAllText(Path.Combine(_dir, "model", "Textures", "Wood.JPG"), "x");
        File.WriteAllText(Path.Combine(_dir, "model", "sub", "Metal.png"), "x");
        File.WriteAllText(Path.Combine(_dir, "elsewhere", "fabric.png"), "x");
        var resolver = new TextureResolver(model, [Path.Combine(_dir, "elsewhere")]);

        // Relative path with wrong case and Windows separators.
        Assert.Equal(Path.Combine(_dir, "model", "sub", "Metal.png"), resolver.Resolve(@"SUB\metal.PNG"));
        // Absolute path from another machine, file now in the sibling Textures folder.
        Assert.Equal(Path.Combine(_dir, "model", "Textures", "Wood.JPG"), resolver.Resolve(@"C:\Users\someone\Desktop\maps\wood.jpg"));
        // Found in a folder the user pointed to.
        Assert.Equal(Path.Combine(_dir, "elsewhere", "fabric.png"), resolver.Resolve("fabric.png"));
        // Percent-encoded glTF URI.
        File.WriteAllText(Path.Combine(_dir, "model", "my texture.png"), "x");
        Assert.Equal(Path.Combine(_dir, "model", "my texture.png"), resolver.Resolve("my%20texture.png"));
        // Missing and embedded references.
        Assert.Null(resolver.Resolve("nothing.png"));
        Assert.Null(resolver.Resolve("*0"));
    }

    [Fact]
    public void Sampler_UsesBottomLeftUvOrigin_AndWraps()
    {
        // 2 × 8 image: top four rows red, bottom four rows blue.
        var px = new byte[2 * 8 * 4];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var i = 4 * (y * 2 + x);
                px[i] = (byte)(y < 4 ? 255 : 0);
                px[i + 2] = (byte)(y < 4 ? 0 : 255);
                px[i + 3] = 255;
            }
        }

        var s = new TextureSampler(new RgbaImage(2, 8, px));
        Assert.True(s.Sample(0.5, 0.8).R > 0.99); // v near 1 → top of the image
        Assert.True(s.Sample(0.5, 0.2).B > 0.99);
        Assert.True(s.Sample(3.5, -0.8).B > 0.99); // wraps (−0.8 ≡ 0.2)
    }

    [Fact]
    public void Palette_FindsTheDominantColors()
    {
        var colors = new List<ColorRgba>();
        var weights = new List<double>();
        var rng = new Random(1);
        for (var i = 0; i < 500; i++)
        {
            colors.Add(ColorRgba.FromSrgb(0.8 + rng.NextDouble() * 0.05, 0.1, 0.1));
            weights.Add(1);
            colors.Add(ColorRgba.FromSrgb(0.1, 0.1, 0.8 + rng.NextDouble() * 0.05));
            weights.Add(1);
        }

        var q = PaletteQuantizer.Quantize(colors, weights, 2);

        Assert.Equal(2, q.Palette.Count);
        Assert.Contains(q.Palette, c => c.ToSrgbBytes().R > 190 && c.ToSrgbBytes().B < 60);
        Assert.Contains(q.Palette, c => c.ToSrgbBytes().B > 190 && c.ToSrgbBytes().R < 60);
        Assert.NotEqual(q.Assignment[0], q.Assignment[1]);
        Assert.Equal(q.Assignment[0], q.Assignment[2]);
    }

    [Fact]
    public void Baker_PaintsEachTriangleWithTheTextureUnderIt()
    {
        // Grid textured with left half red / right half green (UV u = x).
        var mesh = TestMeshes.Grid(8, size: 1.0);
        var tex = new byte[64 * 1 * 4];
        for (var x = 0; x < 64; x++)
        {
            tex[4 * x] = (byte)(x < 32 ? 255 : 0);
            tex[4 * x + 1] = (byte)(x < 32 ? 0 : 255);
            tex[4 * x + 3] = 255;
        }

        var sampler = new TextureSampler(new RgbaImage(64, 1, tex));
        var material = new SceneMaterial { Name = "M", BaseColor = ColorRgba.White, BaseColorTexture = new TextureRef(0) };

        var result = ColorBaker.Bake([mesh], [material], [sampler], 2, "test");

        Assert.Equal(2, result.PaletteSize);
        var red = result.Materials.Single(m => m.IsPaletteEntry && m.Color.R > m.Color.G);
        var green = result.Materials.Single(m => m.IsPaletteEntry && m.Color.G > m.Color.R);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var cx = (mesh.GetVertex(mesh.Indices[3 * t]).X + mesh.GetVertex(mesh.Indices[3 * t + 1]).X + mesh.GetVertex(mesh.Indices[3 * t + 2]).X) / 3;
            if (cx < 0.4)
            {
                Assert.Equal(red, result.Materials[mesh.TriangleMaterials[t]]);
            }
            else if (cx > 0.6)
            {
                Assert.Equal(green, result.Materials[mesh.TriangleMaterials[t]]);
            }
        }
    }

    [Fact]
    public void Baker_MultipliesByBaseColor_AndLeavesUntexturedMaterialsAlone()
    {
        var mesh = TestMeshes.Grid(2);
        mesh.TriangleMaterials[0] = 1; // one triangle uses an untextured material
        var white = new TextureSampler(RgbaImage.Solid(2, 2, 255, 255, 255));
        var tinted = new SceneMaterial { Name = "Tinted", BaseColor = new ColorRgba(0.5, 0, 0), BaseColorTexture = new TextureRef(0) };
        var plain = new SceneMaterial { Name = "Plain", BaseColor = new ColorRgba(0, 0, 1) };

        var result = ColorBaker.Bake([mesh], [tinted, plain], [white, null], 8, "test");

        Assert.Equal(1, mesh.TriangleMaterials[0]); // untouched
        var baked = result.Materials[mesh.TriangleMaterials[1]];
        Assert.True(baked.IsPaletteEntry);
        Assert.Equal(0.5, baked.Color.R, 2);
        Assert.Equal(0.0, baked.Color.G, 2);
    }

    [Fact]
    public void ScaleEstimator_MeasuresWorldUnitsPerUvUnit()
    {
        // 4 × 2 sheet with UVs 0..1: one repeat spans 4 along U and 2 along V.
        var mesh = TestMeshes.Grid(4, size: 1.0);
        for (var i = 0; i < mesh.Positions.Length; i += 3)
        {
            mesh.Positions[i] *= 4;
            mesh.Positions[i + 1] *= 2;
        }

        var scale = TextureScaleEstimator.Estimate([mesh])[0];
        Assert.Equal(4.0, scale.SizeU, 6);
        Assert.Equal(2.0, scale.SizeV, 6);
        Assert.Equal(0.0, TextureScaleEstimator.FractionOutsideUnitSquare([mesh]));

        // Tiling UVs 0..8 over the same sheet: half a unit per repeat along U.
        for (var i = 0; i < mesh.CornerUVs!.Length; i += 2)
        {
            mesh.CornerUVs[i] *= 8;
        }

        Assert.Equal(0.5, TextureScaleEstimator.Estimate([mesh])[0].SizeU, 6);
        Assert.True(TextureScaleEstimator.FractionOutsideUnitSquare([mesh]) > 0.1);
    }

    [Fact]
    public void Store_ExportsRevitFormatsAsIs_ConvertsOthers_AndReportsMissing()
    {
        var model = Path.Combine(_dir, "m.obj");
        File.WriteAllText(model, "");
        var png = PngWriter.Encode(RgbaImage.Solid(2, 2, 1, 2, 3));
        File.WriteAllBytes(Path.Combine(_dir, "a.png"), png);
        byte[] tga = [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 24, 0x20, 9, 8, 7];
        var scene = new SceneModel { SourcePath = model };
        scene.Textures.Add(new SceneTexture { OriginalReference = "a.png", ResolvedPath = Path.Combine(_dir, "a.png") });
        scene.Textures.Add(new SceneTexture { OriginalReference = "*1", EmbeddedData = tga, EmbeddedFormatHint = "tga" });
        scene.Textures.Add(new SceneTexture { OriginalReference = "gone.png" });
        var outDir = Path.Combine(_dir, "out");
        var store = new TextureStore(scene, new ImageDecoder(), outDir);

        var a = store.Export(0);
        var b = store.Export(1);
        var c = store.Export(2);

        Assert.NotNull(a);
        Assert.Equal(png, File.ReadAllBytes(a!));
        Assert.Matches(@"a_[0-9a-f]{12}\.png$", a);
        Assert.NotNull(b);
        Assert.EndsWith(".png", b, StringComparison.Ordinal);
        Assert.Equal("png", ImageFormats.Sniff(File.ReadAllBytes(b!)));
        Assert.Null(c);
        Assert.Contains("gone.png", store.Missing);
        Assert.Equal(2, store.Written);
        Assert.Equal(a, new TextureStore(scene, new ImageDecoder(), outDir).Export(0));
    }
}
