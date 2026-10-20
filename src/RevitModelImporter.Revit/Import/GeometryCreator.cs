using Autodesk.Revit.DB;
using RevitModelImporter.Core.Intermediate;
using RevitModelImporter.Core.Settings;
using RevitException = Autodesk.Revit.Exceptions.ApplicationException;

namespace RevitModelImporter.Revit.Import;

internal sealed class GeometryResult
{
    public List<GeometryObject> Objects { get; } = [];

    /// <summary>Estimated triangles Revit could not use (see docs/DESIGN.md §1.2).</summary>
    public long RejectedTriangles { get; set; }

    public int Solids { get; set; }

    public int Meshes { get; set; }

    public List<string> Issues { get; } = [];
}

/// <summary>Turns one intermediate object into Revit geometry with TessellatedShapeBuilder.
/// One Build() per material group (and per chunk of a large group): when Revit falls back to a Mesh it
/// assigns a single material to the whole mesh, so mixing materials in one build would lose them.</summary>
internal sealed class GeometryCreator
{
    private const int MinimumChunk = 64;
    private readonly GeometryMode _mode;
    private readonly int _maxTrianglesPerBuild;

    public GeometryCreator(GeometryMode mode, int maxTrianglesPerBuild)
    {
        _mode = mode;
        _maxTrianglesPerBuild = Math.Max(MinimumChunk, maxTrianglesPerBuild);
    }

    public GeometryResult Build(IntermediateMesh mesh, IReadOnlyList<ElementId> materialIds, Transform placement)
    {
        var result = new GeometryResult();
        var p = mesh.Positions;
        var vertices = new XYZ[p.Length / 3];
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = placement.OfPoint(new XYZ(p[3 * i], p[3 * i + 1], p[3 * i + 2]));
        }

        var byMaterial = new SortedDictionary<int, List<int>>();
        for (var t = 0; t < mesh.TriangleMaterials.Length; t++)
        {
            var m = mesh.TriangleMaterials[t];
            if (!byMaterial.TryGetValue(m, out var list))
            {
                list = [];
                byMaterial.Add(m, list);
            }

            list.Add(t);
        }

        foreach (var (material, triangles) in byMaterial)
        {
            var materialId = material >= 0 && material < materialIds.Count ? materialIds[material] : ElementId.InvalidElementId;
            for (var start = 0; start < triangles.Count; start += _maxTrianglesPerBuild)
            {
                var count = Math.Min(_maxTrianglesPerBuild, triangles.Count - start);
                BuildChunk(vertices, mesh.Indices, triangles, start, count, materialId, result, 0);
            }
        }

        return result;
    }

    private void BuildChunk(XYZ[] vertices, int[] indices, List<int> triangles, int start, int count, ElementId materialId, GeometryResult result, int depth)
    {
        using var builder = new TessellatedShapeBuilder
        {
            Target = _mode == GeometryMode.MeshOnly ? TessellatedShapeBuilderTarget.Mesh : TessellatedShapeBuilderTarget.AnyGeometry,
            Fallback = _mode == GeometryMode.MeshOnly ? TessellatedShapeBuilderFallback.Salvage : TessellatedShapeBuilderFallback.Mesh,
            GraphicsStyleId = ElementId.InvalidElementId,
        };

        builder.OpenConnectedFaceSet(false);
        for (var k = start; k < start + count; k++)
        {
            var t = triangles[k];
            // A new list per face: TessellatedFace takes ownership and modifies it.
            var loop = new List<XYZ>(3) { vertices[indices[3 * t]], vertices[indices[3 * t + 1]], vertices[indices[3 * t + 2]] };
            builder.AddFace(new TessellatedFace(loop, materialId));
        }

        builder.CloseConnectedFaceSet();

        try
        {
            builder.Build();
        }
        catch (RevitException e) when (count > MinimumChunk && depth < 10)
        {
            // "Too many facets" or inconsistent data: retry in halves so one bad region does not lose everything.
            result.Issues.Add(e.Message);
            var half = count / 2;
            BuildChunk(vertices, indices, triangles, start, half, materialId, result, depth + 1);
            BuildChunk(vertices, indices, triangles, start + half, count - half, materialId, result, depth + 1);
            return;
        }
        catch (RevitException e)
        {
            result.Issues.Add(e.Message);
            result.RejectedTriangles += count;
            return;
        }

        var built = builder.GetBuildResult();
        if (built.Outcome == TessellatedShapeBuilderOutcome.Nothing || !built.AreObjectsAvailable)
        {
            result.RejectedTriangles += count;
            return;
        }

        long unusable = 0;
        for (var set = 0; set < built.GetNumberOfFaceSets(); set++)
        {
            foreach (var issue in built.GetIssuesForFaceSet(set))
            {
                if (issue.MakesDataUnusable())
                {
                    unusable += issue.NumberEncountered;
                }
            }
        }

        var objects = built.GetGeometricalObjects();
        long meshTriangles = 0;
        var allMeshes = objects.Count > 0;
        foreach (var o in objects)
        {
            switch (o)
            {
                case Mesh m:
                    meshTriangles += m.NumTriangles;
                    result.Meshes++;
                    break;
                case Solid:
                    allMeshes = false;
                    result.Solids++;
                    break;
                default:
                    allMeshes = false;
                    break;
            }

            result.Objects.Add(o);
        }

        var rejected = unusable;
        if (allMeshes)
        {
            rejected = Math.Max(rejected, count - meshTriangles);
        }

        result.RejectedTriangles += Math.Clamp(rejected, 0, count);
    }
}
