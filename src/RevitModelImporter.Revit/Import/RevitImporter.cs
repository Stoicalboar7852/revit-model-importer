using System.IO;
using Autodesk.Revit.DB;
using System.Diagnostics;
using RevitModelImporter.Core.Intermediate;
using RevitModelImporter.Core.Presentation;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Settings;
using RevitModelImporter.Revit.Infrastructure;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;
using RevitException = Autodesk.Revit.Exceptions.ApplicationException;

namespace RevitModelImporter.Revit.Import;

/// <summary>Creates materials and DirectShapes from an intermediate model in one TransactionGroup that is
/// assimilated into a single undo step, or rolled back completely on cancel or failure.</summary>
internal sealed class RevitImporter
{
    /// <summary>Elements per transaction: keeps each commit's regeneration bounded and cancel responsive.</summary>
    private const int ElementsPerTransaction = 50;
    private const long TrianglesPerTransaction = 200_000;

    private readonly Document _doc;
    private readonly RevitApplication _app;
    private readonly ImporterSettings _settings;

    public RevitImporter(Document doc, RevitApplication app, ImporterSettings settings)
    {
        _doc = doc;
        _app = app;
        _settings = settings;
    }

    public ImportSummary Import(IntermediateModel model, ImportChoices choices, IImportProgress progress)
    {
        var sw = Stopwatch.StartNew();
        var header = model.Header;
        var summary = new ImportSummary { FileName = Path.GetFileName(header.Source.Path) };
        var warnings = new List<string>();
        var failures = new WarningCollector();
        var categoryId = choices.Category is { } c ? new ElementId(c.Id) : new ElementId(BuiltInCategory.OST_GenericModel);
        if (!DirectShape.IsValidCategoryId(categoryId, _doc))
        {
            categoryId = new ElementId(BuiltInCategory.OST_GenericModel);
        }

        var placement = PlacementTransform(header, choices.Placement);
        var totalTriangles = Math.Max(1, model.Meshes.Sum(m => (long)m.Object.TriangleCount));
        long done = 0;

        using var group = new TransactionGroup(_doc, $"Import 3D model ({summary.FileName})");
        group.Start();
        try
        {
            // Materials first, in their own transaction.
            progress.Report(0, "Creating materials");
            var materialIds = new List<ElementId>(header.Materials.Count);
            using (var t = new Transaction(_doc, "Create materials"))
            {
                failures.Attach(t);
                t.Start();
                var factory = new MaterialFactory(_doc, _app, warnings);
                foreach (var m in header.Materials)
                {
                    if (progress.IsCancellationRequested)
                    {
                        t.RollBack();
                        return Cancel(group, summary);
                    }

                    try
                    {
                        materialIds.Add(factory.GetOrCreate(m));
                    }
                    catch (RevitException e)
                    {
                        warnings.Add($"Material '{m.Name}' could not be created ({e.Message}); its faces use the category material.");
                        materialIds.Add(ElementId.InvalidElementId);
                    }
                }

                if (Commit(t) != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit refused to create the materials.");
                }

                summary.MaterialsCreated = factory.Created;
                summary.MaterialsReused = factory.Reused;
            }

            // Elements, in batches.
            var creator = new GeometryCreator(choices.GeometryMode, _settings.MaxTrianglesPerBuild);
            var index = 0;
            while (index < model.Meshes.Count)
            {
                using var t = new Transaction(_doc, "Create elements");
                failures.Attach(t);
                t.Start();
                var inBatch = 0;
                long batchTriangles = 0;
                while (index < model.Meshes.Count && inBatch < ElementsPerTransaction && batchTriangles < TrianglesPerTransaction)
                {
                    if (progress.IsCancellationRequested)
                    {
                        t.RollBack();
                        return Cancel(group, summary);
                    }

                    var mesh = model.Meshes[index++];
                    progress.Report((double)done / totalTriangles, $"Creating {mesh.Object.Name} ({index} of {model.Meshes.Count})");
                    CreateElement(mesh, materialIds, categoryId, placement, creator, header, summary, warnings);
                    done += mesh.Object.TriangleCount;
                    batchTriangles += mesh.Object.TriangleCount;
                    inBatch++;
                }

                progress.Report((double)done / totalTriangles, "Regenerating");
                if (Commit(t) != TransactionStatus.Committed)
                {
                    warnings.Add("Revit rolled back a batch of elements because of errors.");
                }
            }

            if (progress.IsCancellationRequested)
            {
                return Cancel(group, summary);
            }

            group.Assimilate();
        }
        catch
        {
            if (group.HasStarted())
            {
                group.RollBack();
            }

            throw;
        }

        summary.CreationTime = sw.Elapsed;
        summary.RevitWarningsSuppressed = failures.WarningsSuppressed;
        summary.Warnings.AddRange(warnings);
        if (failures.Messages.Count > 0)
        {
            summary.Warnings.Add("Revit reported: " + string.Join(" | ", failures.Messages.Take(5)) + (failures.Messages.Count > 5 ? " …" : ""));
        }

        return summary;
    }

    private void CreateElement(IntermediateMesh mesh, IReadOnlyList<ElementId> materialIds, ElementId categoryId, Transform placement, GeometryCreator creator, IntermediateHeader header, ImportSummary summary, List<string> warnings)
    {
        summary.TrianglesSentToRevit += mesh.Object.TriangleCount;
        GeometryResult geometry;
        try
        {
            geometry = creator.Build(mesh, materialIds, placement);
        }
        catch (RevitException e)
        {
            warnings.Add($"'{mesh.Object.Name}' could not be built: {e.Message}");
            summary.TrianglesRejectedByRevit += mesh.Object.TriangleCount;
            return;
        }

        summary.TrianglesRejectedByRevit += geometry.RejectedTriangles;
        summary.SolidsCreated += geometry.Solids;
        summary.MeshesCreated += geometry.Meshes;
        if (geometry.Objects.Count == 0)
        {
            warnings.Add($"Revit could not use any of the faces of '{mesh.Object.Name}'.");
            return;
        }

        var ds = DirectShape.CreateElement(_doc, categoryId);
        try
        {
            var shape = geometry.Objects;
            if (!ds.IsValidShape(shape))
            {
                shape = shape.Where(o => ds.IsValidShape(new List<GeometryObject> { o })).ToList();
            }

            if (shape.Count == 0)
            {
                warnings.Add($"Revit rejected the geometry of '{mesh.Object.Name}'.");
                summary.TrianglesRejectedByRevit += mesh.Object.TriangleCount - geometry.RejectedTriangles;
                _doc.Delete(ds.Id);
                return;
            }

            ds.ApplicationId = AddinInfo.ApplicationId;
            ds.ApplicationDataId = $"{header.Source.Path}#{mesh.Object.NodePath}";
            ds.SetShape(shape);
            ds.SetName(RevitNaming.Sanitize(mesh.Object.Name, "Imported object"));
            summary.ElementsCreated++;
        }
        catch (RevitException e)
        {
            warnings.Add($"'{mesh.Object.Name}' could not be created: {e.Message}");
            _doc.Delete(ds.Id);
        }
    }

    private Transform PlacementTransform(IntermediateHeader header, PlacementMode placement)
    {
        switch (placement)
        {
            case PlacementMode.ProjectBasePoint:
                var basePoint = BasePoint.GetProjectBasePoint(_doc);
                return basePoint is null ? Transform.Identity : Transform.CreateTranslation(basePoint.Position);
            case PlacementMode.CenterOnOrigin:
                var min = header.BoundsMin;
                var max = header.BoundsMax;
                return Transform.CreateTranslation(new XYZ(-(min[0] + max[0]) / 2, -(min[1] + max[1]) / 2, -min[2]));
            default:
                return Transform.Identity;
        }
    }

    private static TransactionStatus Commit(Transaction t)
    {
        var status = t.Commit();
        return status;
    }

    private static ImportSummary Cancel(TransactionGroup group, ImportSummary summary)
    {
        group.RollBack();
        summary.Cancelled = true;
        summary.ElementsCreated = 0;
        summary.MaterialsCreated = 0;
        summary.MaterialsReused = 0;
        return summary;
    }
}
