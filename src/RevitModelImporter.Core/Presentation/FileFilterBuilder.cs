using RevitModelImporter.Core.WorkerProtocol;

namespace RevitModelImporter.Core.Presentation;

/// <summary>Builds the file-dialog filter from the worker's list of importable formats.</summary>
public static class FileFilterBuilder
{
    /// <summary>Well-known formats get their own entry near the top; everything else is listed alphabetically.</summary>
    private static readonly string[][] Featured =
    [
        ["obj"], ["fbx"], ["gltf", "glb"], ["dae"], ["stl"], ["ply"], ["3ds"], ["3mf"],
    ];

    public static List<(string Label, string Patterns)> Build(FormatsResult formats)
    {
        var all = formats.Formats.Select(f => f.Extension).ToList();
        var byExt = formats.Formats.ToDictionary(f => f.Extension, f => f.Description, StringComparer.OrdinalIgnoreCase);
        var filters = new List<(string, string)>
        {
            ("All supported files", string.Join(";", all.Select(e => "*." + e))),
        };

        var featured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Featured)
        {
            var present = group.Where(byExt.ContainsKey).ToList();
            if (present.Count == 0)
            {
                continue;
            }

            featured.UnionWith(present);
            var patterns = string.Join(";", present.Select(e => "*." + e));
            filters.Add(($"{byExt[present[0]]} ({patterns})", patterns));
        }

        foreach (var f in formats.Formats.Where(f => !featured.Contains(f.Extension)).OrderBy(f => f.Description, StringComparer.OrdinalIgnoreCase))
        {
            filters.Add(($"{f.Description} (*.{f.Extension})", "*." + f.Extension));
        }

        filters.Add(("All files (*.*)", "*.*"));
        return filters;
    }
}
