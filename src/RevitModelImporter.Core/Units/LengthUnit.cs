using System.Text.Json.Serialization;

namespace RevitModelImporter.Core.Units;

/// <summary>Length units a source model can be authored in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LengthUnit>))]
public enum LengthUnit
{
    Millimeters,
    Centimeters,
    Decimeters,
    Meters,
    Kilometers,
    Inches,
    Feet,
    Yards,
}

public static class LengthUnits
{
    public const double MetersPerFoot = 0.3048;

    public static IReadOnlyList<LengthUnit> All { get; } = Enum.GetValues<LengthUnit>();

    public static double ToMeters(this LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeters => 0.001,
        LengthUnit.Centimeters => 0.01,
        LengthUnit.Decimeters => 0.1,
        LengthUnit.Meters => 1.0,
        LengthUnit.Kilometers => 1000.0,
        LengthUnit.Inches => 0.0254,
        LengthUnit.Feet => MetersPerFoot,
        LengthUnit.Yards => 0.9144,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>Scale factor from this unit to Revit's internal unit (decimal feet).</summary>
    public static double ToFeet(this LengthUnit unit) => unit.ToMeters() / MetersPerFoot;

    public static string Abbreviation(this LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeters => "mm",
        LengthUnit.Centimeters => "cm",
        LengthUnit.Decimeters => "dm",
        LengthUnit.Meters => "m",
        LengthUnit.Kilometers => "km",
        LengthUnit.Inches => "in",
        LengthUnit.Feet => "ft",
        LengthUnit.Yards => "yd",
        _ => unit.ToString(),
    };

    public static string DisplayName(this LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeters => "Millimeters (mm)",
        LengthUnit.Centimeters => "Centimeters (cm)",
        LengthUnit.Decimeters => "Decimeters (dm)",
        LengthUnit.Meters => "Meters (m)",
        LengthUnit.Kilometers => "Kilometers (km)",
        LengthUnit.Inches => "Inches (in)",
        LengthUnit.Feet => "Feet (ft)",
        LengthUnit.Yards => "Yards (yd)",
        _ => unit.ToString(),
    };

    public static bool TryParse(string? text, out LengthUnit unit)
    {
        unit = LengthUnit.Meters;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim().ToLowerInvariant();
        foreach (var u in All)
        {
            var name = u.ToString();
            if (t == u.Abbreviation()
                || string.Equals(t, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, name.TrimEnd('s'), StringComparison.OrdinalIgnoreCase))
            {
                unit = u;
                return true;
            }
        }

        switch (t)
        {
            case "millimetre" or "millimetres":
                unit = LengthUnit.Millimeters;
                return true;
            case "centimetre" or "centimetres":
                unit = LengthUnit.Centimeters;
                return true;
            case "metre" or "metres":
                unit = LengthUnit.Meters;
                return true;
            case "inch":
                unit = LengthUnit.Inches;
                return true;
            case "foot":
                unit = LengthUnit.Feet;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Finds the unit whose size matches <paramref name="metersPerUnit"/> within 0.1 %.</summary>
    public static LengthUnit? FromMetersPerUnit(double metersPerUnit)
    {
        if (!(metersPerUnit > 0) || double.IsInfinity(metersPerUnit))
        {
            return null;
        }

        foreach (var u in All)
        {
            if (Math.Abs(u.ToMeters() / metersPerUnit - 1.0) < 1e-3)
            {
                return u;
            }
        }

        return null;
    }
}
