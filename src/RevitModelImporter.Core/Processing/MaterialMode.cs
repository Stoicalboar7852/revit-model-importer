using System.Text.Json.Serialization;

namespace RevitModelImporter.Core.Processing;

/// <summary>How source materials become Revit materials. Revit cannot use a model's UVs (see docs/DESIGN.md),
/// so each mode is a different compromise.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MaterialMode>))]
public enum MaterialMode
{
    /// <summary>Revit material with the diffuse texture, sized from the mesh's UV density. Best for tiling textures.</summary>
    Textured,

    /// <summary>Colour sampled from the texture per triangle, reduced to a palette of flat materials. Best for atlases and scans.</summary>
    BakedColors,

    /// <summary>One flat colour per source material (base colour, or the texture's average).</summary>
    FlatColors,
}

public static class MaterialModes
{
    public static string DisplayName(this MaterialMode mode) => mode switch
    {
        MaterialMode.Textured => "Textured",
        MaterialMode.BakedColors => "Baked colors",
        MaterialMode.FlatColors => "Flat colors",
        _ => mode.ToString(),
    };

    /// <summary>One-line trade-off shown under the selector in the dialog.</summary>
    public static string Explanation(this MaterialMode mode) => mode switch
    {
        MaterialMode.Textured =>
            "Keeps the image, but Revit projects it onto each face by itself: tiling textures (brick, wood, tiles) look right, UV-unwrapped textures will not line up.",
        MaterialMode.BakedColors =>
            "Paints each face with the texture color under it, using a small palette of flat materials: right colors in the right places, no image detail.",
        MaterialMode.FlatColors =>
            "One plain color per source material (its base color or the texture's average): lightest and fastest, least detail.",
        _ => "",
    };

    public static bool TryParse(string? text, out MaterialMode mode)
    {
        mode = MaterialMode.FlatColors;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "textured" or "texture":
                mode = MaterialMode.Textured;
                return true;
            case "baked" or "baked-colors" or "bakedcolors":
                mode = MaterialMode.BakedColors;
                return true;
            case "flat" or "flat-colors" or "flatcolors":
                mode = MaterialMode.FlatColors;
                return true;
            default:
                return false;
        }
    }

    public static string ToArgument(this MaterialMode mode) => mode switch
    {
        MaterialMode.Textured => "textured",
        MaterialMode.BakedColors => "baked",
        _ => "flat",
    };
}
