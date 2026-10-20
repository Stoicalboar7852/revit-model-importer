using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitModelImporter.Core.Settings;

/// <summary>Loads and saves <see cref="ImporterSettings"/>. A missing or unreadable file yields defaults;
/// an unreadable file is kept as settings.json.bad so nothing the user wrote is lost silently.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SettingsStore(string path) => FilePath = path;

    public string FilePath { get; }

    /// <summary>%AppData%\RevitModelImporter\settings.json (per user, roams with the profile).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitModelImporter", "settings.json");

    /// <summary>%LocalAppData%\RevitModelImporter\textures — used while the project has not been saved.</summary>
    public static string DefaultFallbackTextureFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelImporter", "textures");

    public string? LastError { get; private set; }

    public ImporterSettings Load()
    {
        LastError = null;
        if (!File.Exists(FilePath))
        {
            return new ImporterSettings().Normalize();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<ImporterSettings>(json, Options) ?? new ImporterSettings();
            return settings.Normalize();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = $"Could not read {FilePath}: {e.Message}. Default settings are used.";
            try
            {
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }

            return new ImporterSettings().Normalize();
        }
    }

    public void Save(ImporterSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Normalize(), Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static string Serialize(ImporterSettings settings) => JsonSerializer.Serialize(settings, Options);
}
