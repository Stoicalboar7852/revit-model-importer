using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitModelImporter.Core.Presentation;
using RevitModelImporter.Core.Settings;
using RevitModelImporter.Revit.Infrastructure;
using RevitModelImporter.Revit.UI;

namespace RevitModelImporter.Revit.Commands;

/// <summary>Opens the settings dialog (thresholds, recommendation on/off, texture folder).</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var owner = commandData.Application.MainWindowHandle;
        var store = new SettingsStore(SettingsStore.DefaultPath);
        var settings = store.Load();
        SettingsWindow? window = null;
        var viewModel = new SettingsViewModel(settings, store.FilePath, () =>
        {
            var dialog = new OpenFolderDialog { Title = "Texture folder for unsaved projects" };
            return (window is not null ? dialog.ShowDialog(window) : dialog.ShowDialog()) == true ? dialog.FolderName : null;
        });
        window = new SettingsWindow(viewModel).OwnedBy(owner);
        if (window.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        try
        {
            store.Save(settings);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            ImportModelCommand.ShowError("Settings could not be saved", e.Message, store.FilePath);
            return Result.Failed;
        }

        return Result.Succeeded;
    }
}
