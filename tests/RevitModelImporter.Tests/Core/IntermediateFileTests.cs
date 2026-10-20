using RevitModelImporter.Core.Intermediate;
using RevitModelImporter.Core.Processing;

namespace RevitModelImporter.Tests.Core;

public sealed class IntermediateFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rmi-ir-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static (IntermediateHeader, List<ObjectMesh>) Sample()
    {
        var header = new IntermediateHeader
        {
            Generator = "test",
            MaterialMode = MaterialMode.Textured,
            Source = new SourceInfo { Path = "/m/model.obj", FormatId = "obj", SizeBytes = 123 },
            Materials =
            [
                new IntermediateMaterial { Name = "A", Key = "k1", Color = [1, 2, 3], DiffuseTexture = "/t/a.png", TextureSizeX = 3.2, TextureSizeY = 1.6 },
                new IntermediateMaterial { Name = "B", Key = "k2", Kind = MaterialKind.Baked, Opacity = 0.5 },
            ],
        };
        header.Stats.SourceTriangles = 3;
        header.Warnings.Add("hello");
        var meshes = new List<ObjectMesh>
        {
            new() { Name = "One", NodePath = "Root/One", Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0, 1e9, 2e9, -3.25], Indices = [0, 1, 2, 1, 3, 2], TriangleMaterials = [0, 1] },
            new() { Name = "Two", NodePath = "Root/Two", Positions = [5, 5, 5, 6, 5, 5, 5, 6, 5], Indices = [0, 1, 2], TriangleMaterials = [1] },
        };
        return (header, meshes);
    }

    [Fact]
    public void RoundTrips_HeaderAndBuffersExactly()
    {
        var (header, meshes) = Sample();
        var path = Path.Combine(_dir, "a.rmi");
        IntermediateFile.Write(path, header, meshes);

        var model = IntermediateFile.Read(path);

        Assert.Equal(IntermediateHeader.CurrentVersion, model.Header.Version);
        Assert.Equal(MaterialMode.Textured, model.Header.MaterialMode);
        Assert.Equal(2, model.Header.Materials.Count);
        Assert.Equal("/t/a.png", model.Header.Materials[0].DiffuseTexture);
        Assert.Equal(MaterialKind.Baked, model.Header.Materials[1].Kind);
        Assert.Equal(["hello"], model.Header.Warnings);
        Assert.Equal(2, model.Meshes.Count);
        Assert.Equal(meshes[0].Positions, model.Meshes[0].Positions);
        Assert.Equal(meshes[0].Indices, model.Meshes[0].Indices);
        Assert.Equal(meshes[1].TriangleMaterials, model.Meshes[1].TriangleMaterials);
        Assert.Equal("Root/Two", model.Meshes[1].Object.NodePath);
        Assert.Equal([0, 0, -3.25], model.Header.BoundsMin);
        Assert.Equal([1e9, 2e9, 5], model.Header.BoundsMax);
    }

    [Fact]
    public void Header_IsJsonAfterAFixedPrefix()
    {
        var (header, meshes) = Sample();
        var path = Path.Combine(_dir, "b.rmi");
        IntermediateFile.Write(path, header, meshes);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RMI1"u8.ToArray(), bytes[..4]);
        Assert.Equal((byte)'{', bytes[16]);
        Assert.Equal("test", IntermediateFile.ReadHeader(path).Generator);
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        var path = Path.Combine(_dir, "c.rmi");
        File.WriteAllText(path, "not an rmi file at all");
        Assert.Throws<InvalidDataException>(() => IntermediateFile.Read(path));
    }

    [Fact]
    public void RejectsNewerVersions()
    {
        var (header, meshes) = Sample();
        header.Version = IntermediateHeader.CurrentVersion + 1;
        var path = Path.Combine(_dir, "d.rmi");
        IntermediateFile.Write(path, header, meshes);
        var e = Assert.Throws<InvalidDataException>(() => IntermediateFile.Read(path));
        Assert.Contains("newer", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsOutOfRangeIndices()
    {
        var (header, meshes) = Sample();
        meshes[1].Indices = [0, 1, 7];
        var path = Path.Combine(_dir, "e.rmi");
        IntermediateFile.Write(path, header, meshes);
        Assert.Throws<InvalidDataException>(() => IntermediateFile.Read(path));
    }

    [Fact]
    public void RejectsTruncatedFiles()
    {
        var (header, meshes) = Sample();
        var path = Path.Combine(_dir, "f.rmi");
        IntermediateFile.Write(path, header, meshes);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^20]);
        Assert.ThrowsAny<Exception>(() => IntermediateFile.Read(path));
    }
}
