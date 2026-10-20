using System.Globalization;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Simplification;
using RevitModelImporter.Core.Units;

namespace RevitModelImporter.Core.WorkerProtocol;

/// <summary>Everything the worker needs for <c>convert</c>; maps 1:1 onto its command line.</summary>
public sealed class ConvertRequest
{
    public required string InputPath { get; init; }

    public required string OutputPath { get; init; }

    public LengthUnit Unit { get; init; } = LengthUnit.Meters;

    public UpAxis UpAxis { get; init; } = UpAxis.YUp;

    public MaterialMode MaterialMode { get; init; } = MaterialMode.FlatColors;

    public SimplifyTarget? Simplify { get; init; }

    public int PaletteSize { get; init; } = 32;

    /// <summary>Folder where textures are written for Revit to reference.</summary>
    public string? TextureDirectory { get; init; }

    public IReadOnlyList<string> TextureSearchFolders { get; init; } = [];

    /// <summary>Revit Application.VertexTolerance, feet.</summary>
    public double? VertexTolerance { get; init; }

    /// <summary>Revit Application.ShortCurveTolerance, feet.</summary>
    public double? MinEdgeLength { get; init; }

    /// <summary>Merge all objects into one.</summary>
    public bool Merge { get; init; }

    public List<string> ToArguments()
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "convert", InputPath,
            "--out", OutputPath,
            "--units", Unit.Abbreviation(),
            "--up", UpAxis == UpAxis.YUp ? "y" : "z",
            "--material-mode", MaterialMode.ToArgument(),
            "--palette-size", PaletteSize.ToString(inv),
        };
        if (Simplify is { } s)
        {
            args.Add("--simplify");
            args.Add(s.ToArgument());
        }

        if (TextureDirectory is not null)
        {
            args.Add("--texture-dir");
            args.Add(TextureDirectory);
        }

        foreach (var f in TextureSearchFolders)
        {
            args.Add("--texture-search");
            args.Add(f);
        }

        if (VertexTolerance is { } vt)
        {
            args.Add("--vertex-tolerance");
            args.Add(vt.ToString("R", inv));
        }

        if (MinEdgeLength is { } me)
        {
            args.Add("--min-edge");
            args.Add(me.ToString("R", inv));
        }

        if (Merge)
        {
            args.Add("--merge");
        }

        return args;
    }
}
