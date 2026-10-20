using RevitModelImporter.Core.Units;

namespace RevitModelImporter.Core.Processing;

/// <summary>Per-format conventions used when a file does not say.</summary>
public static class FormatDefaults
{
    /// <summary>Extensions left out of the file filter: Revit imports these natively (don't compete), or
    /// they never contain static geometry, or they are ambiguous.</summary>
    public static readonly IReadOnlySet<string> ExcludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Revit-native
        "skp", "3dm", "ifc", "ifczip", "dwg", "dxf", "sat", "step", "stp", "rvt", "rfa", "nwd", "nwc",
        // animation / motion only
        "bvh", "csm", "md5anim", "md5camera", "mot",
        // ambiguous generic container
        "xml",
    };

    private static readonly HashSet<string> ZUpFormats = new(StringComparer.OrdinalIgnoreCase) { "stl", "ply", "3mf", "3ds", "off", "amf", "ase", "ask", "prj", "lwo", "lxo", "lws" };

    private static readonly HashSet<string> MillimeterFormats = new(StringComparer.OrdinalIgnoreCase) { "stl", "3mf", "ply", "amf", "off" };

    public static UpAxis DefaultUpAxis(string formatId) => ZUpFormats.Contains(formatId) ? UpAxis.ZUp : UpAxis.YUp;

    /// <summary>A guess for formats without unit information; the dialog marks it as a guess.</summary>
    public static LengthUnit GuessUnit(string formatId) => MillimeterFormats.Contains(formatId) ? LengthUnit.Millimeters : LengthUnit.Meters;

    public static string NormalizeExtension(string pathOrExtension)
    {
        var ext = pathOrExtension.Contains('.', StringComparison.Ordinal) ? Path.GetExtension(pathOrExtension) : pathOrExtension;
        return ext.TrimStart('.').ToLowerInvariant();
    }
}
