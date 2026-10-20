using System.Text;

namespace RevitModelImporter.Core.Processing;

/// <summary>Element-name rules shared by the add-in and tests. Revit rejects names containing
/// { } [ ] | ; &lt; &gt; ? ` ~ (and control characters); the add-in still double-checks with NamingUtils.IsValidName.</summary>
public static class RevitNaming
{
    public const int MaxLength = 120;

    private static readonly char[] Prohibited = ['{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\\'];

    public static bool IsProhibited(char c) => Array.IndexOf(Prohibited, c) >= 0 || char.IsControl(c);

    /// <summary>Replaces prohibited characters, collapses whitespace and trims to <see cref="MaxLength"/>.</summary>
    public static string Sanitize(string? name, string fallback = "Imported")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var sb = new StringBuilder(name.Length);
        var lastWasSpace = false;
        foreach (var c in name)
        {
            var ch = char.IsWhiteSpace(c) ? ' ' : IsProhibited(c) ? '_' : c;
            if (ch == ' ')
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            sb.Append(ch);
        }

        var s = sb.ToString().Trim();
        if (s.Length > MaxLength)
        {
            s = s[..MaxLength].TrimEnd();
        }

        return s.Length == 0 ? fallback : s;
    }

    /// <summary>"Name", "Name (2)", "Name (3)" … until <paramref name="isTaken"/> returns false.</summary>
    public static string MakeUnique(string name, Func<string, bool> isTaken)
    {
        if (!isTaken(name))
        {
            return name;
        }

        for (var i = 2; i < 100_000; i++)
        {
            var suffix = $" ({i})";
            var stem = name.Length + suffix.Length > MaxLength ? name[..(MaxLength - suffix.Length)] : name;
            var candidate = stem + suffix;
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        return name + " " + Guid.NewGuid().ToString("N")[..8];
    }
}
