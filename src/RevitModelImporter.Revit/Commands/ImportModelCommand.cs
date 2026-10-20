using System.Diagnostics;
using System.IO;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitModelImporter.Core.Intermediate;
using RevitModelImporter.Core.Presentation;
using RevitModelImporter.Core.Settings;
using RevitModelImporter.Core.WorkerProtocol;
using RevitModelImporter.Revit.Import;
using RevitModelImporter.Revit.Infrastructure;
using RevitModelImporter.Revit.UI;

namespace RevitModelImporter.Revit.Commands;

/// <summary>
/// Import 3D Model: pick a file → analyze it in the worker (cancellable) → Import dialog → convert in the worker
/// (cancellable) → create materials and DirectShapes here, in the command's API context, inside one
/// TransactionGroup → summary.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ImportModelCommand : IExternalCommand
{
    private static FormatsResult? s_formats;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiapp = commandData.Application;
        var owner = uiapp.MainWindowHandle;
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc is null)
        {
            return Result.Cancelled;
        }

        try
        {
            return Run(uiapp, doc, owner);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (WorkerException e)
        {
            ShowError("The model could not be imported", e.Message, e.Details);
            return Result.Cancelled;
        }
        catch (InvalidDataException e)
        {
            ShowError("The converted model could not be read", e.Message, e.ToString());
            return Result.Cancelled;
        }
        catch (FileNotFoundException e)
        {
            ShowError("A file is missing", e.Message, e.FileName);
            return Result.Cancelled;
        }
        catch (Exception e)
        {
            ShowError("Unexpected error", e.Message, e.ToString());
            message = e.Message;
            return Result.Failed;
        }
    }

    private static Result Run(UIApplication uiapp, Document doc, IntPtr owner)
    {
        var store = new SettingsStore(SettingsStore.DefaultPath);
        var settings = store.Load();
        var startupWarnings = new List<string>();
        if (store.LastError is { } settingsError)
        {
            startupWarnings.Add(settingsError);
        }

        var workerDir = WorkerLocator.FindWorkerDirectory(AddinInfo.AssemblyDirectory)
            ?? throw new FileNotFoundException($"The converter was not found next to the add-in ({Path.Combine(AddinInfo.AssemblyDirectory, WorkerLocator.WorkerFolderName)}). Please reinstall Revit Model Importer.");
        var client = new WorkerClient(WorkerLocator.CreateLaunchInfo(workerDir));

        // 1. File picker, filter built from the loader's own list of formats.
        s_formats ??= ProgressWindow.Run(owner, "Starting the converter…", (_, ct) => client.GetFormatsAsync(ct));
        var file = NativeFileDialog.OpenFile(owner, "Import 3D Model", FileFilterBuilder.Build(s_formats), settings.LastModelFolder);
        if (file is null)
        {
            return Result.Cancelled;
        }

        settings.LastModelFolder = Path.GetDirectoryName(file);
        TrySave(store, settings);

        // 2. Analyze (cancellable; Cancel kills the worker).
        var analysisTimer = Stopwatch.StartNew();
        var analysis = ProgressWindow.Run(owner, $"Analyzing {Path.GetFileName(file)}…",
            (progress, ct) => client.AnalyzeAsync(file, settings.TextureSearchFolders, progress, ct));
        analysisTimer.Stop();

        // 3. Import dialog.
        var (textureFolder, nextToProject) = RevitEnvironment.TextureFolder(doc, settings);
        ImportDialog? dialog = null;
        var context = new ImportDialogContext
        {
            Categories = RevitEnvironment.DirectShapeCategories(doc),
            ShortCurveToleranceFeet = uiapp.Application.ShortCurveTolerance,
            TextureFolder = textureFolder,
            TextureFolderIsNextToProject = nextToProject,
            PickFolder = () => PickFolder(dialog, "Locate textures folder"),
            Reanalyze = (folders, ct) => client.AnalyzeAsync(file, folders, null, ct),
        };
        var viewModel = new ImportDialogViewModel(analysis, settings, context);
        dialog = new ImportDialog(viewModel).OwnedBy(owner);
        if (dialog.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        viewModel.SaveTo(settings);
        TrySave(store, settings);
        var choices = viewModel.GetChoices();

        // 4. Convert in the worker (cancellable).
        var tempFile = Path.Combine(Path.GetTempPath(), "RevitModelImporter", Guid.NewGuid().ToString("N") + IntermediateFile.Extension);
        try
        {
            var request = new ConvertRequest
            {
                InputPath = file,
                OutputPath = tempFile,
                Unit = choices.Unit,
                UpAxis = choices.UpAxis,
                MaterialMode = choices.MaterialMode,
                PaletteSize = choices.PaletteSize,
                Simplify = choices.Simplify,
                TextureDirectory = textureFolder,
                TextureSearchFolders = choices.TextureSearchFolders,
                VertexTolerance = uiapp.Application.VertexTolerance,
                MinEdgeLength = uiapp.Application.ShortCurveTolerance,
                Merge = choices.Grouping == GroupingMode.SingleElement,
            };
            var conversionTimer = Stopwatch.StartNew();
            var conversion = ProgressWindow.Run(owner, $"Converting {Path.GetFileName(file)}…", (progress, ct) => client.ConvertAsync(request, progress, ct));
            var model = IntermediateFile.Read(conversion.OutputPath);
            conversionTimer.Stop();

            // 5. Create elements here, in the command's API context.
            ImportSummary summary;
            using (var progress = new BackgroundProgress($"Creating elements from {Path.GetFileName(file)}…", owner))
            {
                summary = new RevitImporter(doc, uiapp.Application, settings).Import(model, choices, progress);
            }

            var stats = model.Header.Stats;
            summary.SourceTriangles = stats.SourceTriangles;
            summary.Simplified = stats.Simplified;
            summary.TrianglesAfterSimplify = stats.TrianglesAfterSimplify;
            summary.TrianglesRemovedByCleanup = stats.DegenerateTrianglesRemoved + stats.DuplicateTrianglesRemoved + stats.ShortEdgeTrianglesRemoved;
            summary.MissingTextures = stats.MissingTextures;
            summary.TextureFolder = stats.TexturesWritten > 0 || model.Header.Materials.Any(m => m.DiffuseTexture is not null) ? textureFolder : null;
            summary.AnalysisTime = analysisTimer.Elapsed;
            summary.ConversionTime = conversionTimer.Elapsed;
            summary.Warnings.InsertRange(0, startupWarnings.Concat(conversion.Warnings));

            new SummaryWindow(summary).OwnedBy(owner).ShowDialog();
            return summary.Cancelled ? Result.Cancelled : Result.Succeeded;
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    private static string? PickFolder(Window? owner, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        var ok = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FolderName : null;
    }

    private static void TrySave(SettingsStore store, ImporterSettings settings)
    {
        try
        {
            store.Save(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Revit Model Importer: could not save settings: {e.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp file; Windows cleans %TEMP% eventually.
        }
    }

    internal static void ShowError(string title, string text, string? details)
    {
        var dialog = new TaskDialog(AddinInfo.Name)
        {
            MainIcon = TaskDialogIcon.TaskDialogIconWarning,
            MainInstruction = title,
            MainContent = text,
        };
        if (!string.IsNullOrWhiteSpace(details))
        {
            dialog.ExpandedContent = details.Length > 4000 ? details[^4000..] : details;
        }

        dialog.Show();
    }
}
