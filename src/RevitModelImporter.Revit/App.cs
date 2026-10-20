using Autodesk.Revit.UI;
using RevitModelImporter.Revit.Commands;
using RevitModelImporter.Revit.Infrastructure;

namespace RevitModelImporter.Revit;

/// <summary>Creates the "Model Importer" ribbon tab.</summary>
public sealed class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab(AddinInfo.RibbonTab);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // The tab already exists (another add-in with the same tab name, or a reload).
        }

        var panel = application.CreateRibbonPanel(AddinInfo.RibbonTab, "Import");
        var assembly = typeof(App).Assembly.Location;

        var import = new PushButtonData("RevitModelImporter.Import", "Import\n3D Model", assembly, typeof(ImportModelCommand).FullName)
        {
            ToolTip = "Import an OBJ, FBX, glTF/GLB, DAE, STL, PLY, 3DS, 3MF or other 3D model as DirectShape elements, with its materials and textures.",
            LongDescription = "The file is analyzed first. You then choose units, orientation, placement, category, material mode and optional simplification. The whole import is a single undoable step.",
            AvailabilityClassName = typeof(DocumentAvailability).FullName,
            LargeImage = IconFactory.Import(32),
            Image = IconFactory.Import(16),
        };
        panel.AddItem(import);

        var settings = new PushButtonData("RevitModelImporter.Settings", "Settings", assembly, typeof(SettingsCommand).FullName)
        {
            ToolTip = "Simplification recommendation thresholds, texture folder and other importer settings.",
            LargeImage = IconFactory.Settings(32),
            Image = IconFactory.Settings(16),
        };
        panel.AddItem(settings);

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}
