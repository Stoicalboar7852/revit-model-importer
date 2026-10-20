using Autodesk.Revit.DB;
namespace RevitModelImporter.Revit.Import;

internal interface IImportProgress
{
    void Report(double fraction, string message);

    bool IsCancellationRequested { get; }
}
