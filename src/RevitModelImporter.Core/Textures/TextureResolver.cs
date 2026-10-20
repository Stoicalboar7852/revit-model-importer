namespace RevitModelImporter.Core.Textures;

/// <summary>Finds external texture files the way people actually ship models: paths written on another OS,
/// wrong case, textures moved into a sibling folder, or in a folder the user points to.</summary>
public sealed class TextureResolver
{
    /// <summary>Sibling folder names searched next to the model file (compared case-insensitively).</summary>
    public static readonly IReadOnlyList<string> SiblingFolderNames = ["textures", "texture", "tex", "maps", "images", "materials"];

    private readonly string _modelDirectory;
    private readonly IReadOnlyList<string> _extraFolders;
    private readonly Dictionary<string, string[]> _directoryListing = new(StringComparer.Ordinal);

    public TextureResolver(string modelPath, IEnumerable<string>? extraSearchFolders = null)
    {
        _modelDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? ".";
        _extraFolders = (extraSearchFolders ?? []).Where(Directory.Exists).Select(Path.GetFullPath).ToList();
    }

    /// <summary>Returns the full path of the texture, or null if it cannot be found.</summary>
    public string? Resolve(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('*'))
        {
            return null;
        }

        var cleaned = Normalize(reference);

        // 1. As written: absolute, or relative to the model file.
        var direct = Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(_modelDirectory, cleaned);
        if (File.Exists(direct))
        {
            return Path.GetFullPath(direct);
        }

        // 2. Relative path, matching each segment case-insensitively (files from Windows on Linux and back).
        if (!Path.IsPathRooted(cleaned) && FindCaseInsensitive(_modelDirectory, cleaned) is { } relative)
        {
            return relative;
        }

        var fileName = FileNameOf(cleaned);
        if (fileName.Length == 0)
        {
            return null;
        }

        // 3. Just the file name in the model folder, its sibling texture folders and user-supplied folders.
        foreach (var folder in CandidateFolders())
        {
            if (FindInDirectory(folder, fileName) is { } hit)
            {
                return hit;
            }
        }

        // 4. Absolute paths from another machine: try the trailing segments relative to the model folder
        //    (C:\Users\me\project\maps\wood.jpg → maps\wood.jpg).
        var segments = cleaned.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var take = Math.Min(3, segments.Length - 1); take >= 2; take--)
        {
            var tail = Path.Combine(segments[^take..]);
            if (FindCaseInsensitive(_modelDirectory, tail) is { } t)
            {
                return t;
            }
        }

        return null;
    }

    private IEnumerable<string> CandidateFolders()
    {
        yield return _modelDirectory;
        foreach (var sub in SafeDirectories(_modelDirectory))
        {
            if (SiblingFolderNames.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
            {
                yield return sub;
            }
        }

        var parent = Path.GetDirectoryName(_modelDirectory);
        if (parent is not null)
        {
            foreach (var sub in SafeDirectories(parent))
            {
                if (SiblingFolderNames.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                {
                    yield return sub;
                }
            }
        }

        foreach (var extra in _extraFolders)
        {
            yield return extra;
            foreach (var sub in SafeDirectories(extra))
            {
                yield return sub;
            }
        }
    }

    private string? FindCaseInsensitive(string baseDir, string relative)
    {
        var current = baseDir;
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var isLast = i == parts.Length - 1;
            var match = isLast ? FindInDirectory(current, part) : FindSubdirectory(current, part);
            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return File.Exists(current) ? current : null;
    }

    private string? FindInDirectory(string dir, string fileName)
    {
        var exact = Path.Combine(dir, fileName);
        if (File.Exists(exact))
        {
            return exact;
        }

        foreach (var f in List(dir))
        {
            if (string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }

        return null;
    }

    private static string? FindSubdirectory(string dir, string name)
    {
        var exact = Path.Combine(dir, name);
        if (Directory.Exists(exact))
        {
            return exact;
        }

        return SafeDirectories(dir).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
    }

    private string[] List(string dir)
    {
        if (!_directoryListing.TryGetValue(dir, out var files))
        {
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                files = [];
            }

            _directoryListing[dir] = files;
        }

        return files;
    }

    private static string[] SafeDirectories(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static string Normalize(string reference)
    {
        var r = reference.Trim().Trim('"');
        if (r.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            r = Uri.UnescapeDataString(r[7..]);
        }
        else if (r.Contains('%', StringComparison.Ordinal))
        {
            // glTF URIs are percent-encoded ("my%20texture.png").
            r = Uri.UnescapeDataString(r);
        }

        r = r.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        // A Windows drive path seen on Linux ("C:/models/tex.png") is not rooted there; keep it as relative tail.
        if (Path.DirectorySeparatorChar == '/' && r.Length > 2 && r[1] == ':')
        {
            r = r[2..].TrimStart(Path.DirectorySeparatorChar);
            r = Path.DirectorySeparatorChar + r;
        }

        return r;
    }

    private static string FileNameOf(string path)
    {
        var i = path.LastIndexOf(Path.DirectorySeparatorChar);
        return i >= 0 ? path[(i + 1)..] : path;
    }
}
