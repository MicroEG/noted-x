using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NotedX.Core.Storage;

namespace NotedX.Desktop.Settings;

/// <summary>Loads and atomically saves <see cref="DesktopPreferences"/> next to the database.</summary>
public sealed class PreferencesStore
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly ILogger<PreferencesStore> _logger;

    /// <summary>Creates a store rooted at the application data directory.</summary>
    public PreferencesStore(AppPaths paths, ILogger<PreferencesStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Absolute path of the preferences file.</summary>
    public string FilePath => Path.Combine(_paths.Root, FileName);

    /// <summary>Loads preferences, returning defaults when the file is missing or unreadable.</summary>
    public DesktopPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new DesktopPreferences();
            }

            var json = File.ReadAllText(FilePath);
            var preferences = JsonSerializer.Deserialize<DesktopPreferences>(json, JsonOptions)
                ?? new DesktopPreferences();
            preferences.Theme = preferences.NormalizedTheme();
            return preferences;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read desktop preferences; defaults will be used");
            return new DesktopPreferences();
        }
    }

    /// <summary>Writes preferences to disk.</summary>
    /// <returns><c>true</c> when the file was written.</returns>
    public bool Save(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Theme = preferences.NormalizedTheme();

        try
        {
            Directory.CreateDirectory(_paths.Root);
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save desktop preferences");
            return false;
        }
    }
}
