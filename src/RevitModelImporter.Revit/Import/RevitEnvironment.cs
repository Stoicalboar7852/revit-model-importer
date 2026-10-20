using System.IO;
using Autodesk.Revit.DB;
using RevitModelImporter.Core.Presentation;
using RevitModelImporter.Core.Settings;

namespace RevitModelImporter.Revit.Import;

/// <summary>Reads the document-dependent inputs of the Import dialog.</summary>
internal static class RevitEnvironment
{
    /// <summary>Model categories that DirectShape accepts in this document (DirectShape.IsValidCategoryId).</summary>
    public static IReadOnlyList<CategoryOption> DirectShapeCategories(Document doc)
    {
        var result = new List<CategoryOption>();
        foreach (Category cat in doc.Settings.Categories)
        {
            if (cat is null || cat.CategoryType != CategoryType.Model || cat.IsTagCategory)
            {
                continue;
            }

            if (!DirectShape.IsValidCategoryId(cat.Id, doc))
            {
                continue;
            }

            result.Add(new CategoryOption(cat.Id.Value, cat.Name, cat.BuiltInCategory.ToString()));
        }

        return result.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Where textures are copied: next to the saved project, else the user's fallback folder.</summary>
    public static (string Folder, bool NextToProject) TextureFolder(Document doc, ImporterSettings settings)
    {
        var fallback = string.IsNullOrWhiteSpace(settings.FallbackTextureFolder) ? SettingsStore.DefaultFallbackTextureFolder : settings.FallbackTextureFolder!;
        if (settings.AlwaysUseFallbackTextureFolder)
        {
            return (fallback, false);
        }

        var path = doc.PathName;
        if (string.IsNullOrWhiteSpace(path) || doc.IsModelInCloud)
        {
            return (fallback, false);
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return (fallback, false);
            }

            return (Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "_textures"), true);
        }
        catch (ArgumentException)
        {
            return (fallback, false);
        }
    }
}
