using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitModelImporter.Revit.Commands;

/// <summary>The import button needs an open, editable document.</summary>
public sealed class DocumentAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
    {
        var doc = applicationData?.ActiveUIDocument?.Document;
        return doc is not null && !doc.IsReadOnly && !doc.IsLinked;
    }
}
