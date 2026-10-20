using System.Text.Json.Serialization;
using RevitModelImporter.Core.Geometry;

namespace RevitModelImporter.Core.Units;

/// <summary>Which source axis points up. Revit is Z-up.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpAxis>))]
public enum UpAxis
{
    YUp,
    ZUp,
}

public static class AxisConversion
{
    /// <summary>Transform that maps the source up axis onto Revit's Z axis.
    /// Y-up to Z-up is a +90° rotation about X: (x, y, z) → (x, −z, y). This keeps the
    /// coordinate system right-handed, so triangle winding is preserved.</summary>
    public static Mat4 ToZUp(UpAxis up) => up switch
    {
        UpAxis.YUp => new Mat4(
            1, 0, 0, 0,
            0, 0, -1, 0,
            0, 1, 0, 0,
            0, 0, 0, 1),
        UpAxis.ZUp => Mat4.Identity,
        _ => throw new ArgumentOutOfRangeException(nameof(up), up, null),
    };

    /// <summary>Full source → Revit transform: axis conversion followed by unit scaling to feet.</summary>
    public static Mat4 SourceToRevit(UpAxis up, LengthUnit unit) => Mat4.Scale(unit.ToFeet()) * ToZUp(up);

    public static string DisplayName(this UpAxis up) => up == UpAxis.YUp ? "Y-up" : "Z-up";

    public static bool TryParse(string? text, out UpAxis up)
    {
        up = UpAxis.YUp;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "y" or "y-up" or "yup":
                up = UpAxis.YUp;
                return true;
            case "z" or "z-up" or "zup":
                up = UpAxis.ZUp;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Width (X), depth and height of a source-space box once the up axis is applied.</summary>
    public static Vec3 SizeInRevitAxes(Vec3 sourceSize, UpAxis up) =>
        up == UpAxis.YUp ? new Vec3(sourceSize.X, sourceSize.Z, sourceSize.Y) : sourceSize;
}
