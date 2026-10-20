using System.IO;
using System.Reflection;

namespace RevitModelImporter.Revit.Infrastructure;

internal static class AddinInfo
{
    /// <summary>AddInId of the application entry in every .addin manifest. Never change it: Revit uses it to
    /// identify the add-in (and DirectShape.ApplicationId records it).</summary>
    public const string AddInId = "d31302fc-ca11-4cf4-88e6-2d1526ad2673";

    public const string VendorId = "RevitModelImporter";

    public const string Name = "Revit Model Importer";

    public const string RibbonTab = "Model Importer";

    public static string Version =>
        typeof(AddinInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    /// <summary>Folder containing this assembly (…\Addins\&lt;year&gt;\RevitModelImporter).</summary>
    public static string AssemblyDirectory => Path.GetDirectoryName(typeof(AddinInfo).Assembly.Location) ?? AppContext.BaseDirectory;

    /// <summary>"RevitModelImporter/{AddInId}" — stored in DirectShape.ApplicationId.</summary>
    public static string ApplicationId => $"RevitModelImporter/{AddInId}";
}
