using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using NotedX.Desktop.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NotedX.Desktop.Services;

/// <summary>Applies the light or dark appearance and persists the user's choice.</summary>
public sealed class ThemeService
{
    private static readonly System.Windows.Media.Color AccentColor =
        System.Windows.Media.Color.FromRgb(0x63, 0x66, 0xF1);

    private readonly PreferencesStore _store;
    private readonly DesktopPreferences _preferences;
    private readonly ILogger<ThemeService> _logger;

    /// <summary>Creates the service over the saved preferences.</summary>
    public ThemeService(
        PreferencesStore store,
        DesktopPreferences preferences,
        ILogger<ThemeService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether dark mode is active.</summary>
    public bool IsDark { get; private set; }

    /// <summary>Raised after the theme has been applied.</summary>
    public event EventHandler? Changed;

    /// <summary>Applies the saved theme without changing it.</summary>
    public void Initialize()
    {
        Apply(_preferences.NormalizedTheme(), persist: false);
    }

    /// <summary>Switches between light and dark mode.</summary>
    public void Toggle()
    {
        SetTheme(IsDark ? "Light" : "Dark");
    }

    /// <summary>Applies and stores a theme name.</summary>
    public void SetTheme(string themeName)
    {
        var normalized = string.Equals(themeName, "Dark", StringComparison.OrdinalIgnoreCase)
            ? "Dark"
            : "Light";

        _preferences.Theme = normalized;
        Apply(normalized, persist: true);
    }

    private void Apply(string themeName, bool persist)
    {
        var isDark = string.Equals(themeName, "Dark", StringComparison.OrdinalIgnoreCase);

        try
        {
            var theme = isDark ? ApplicationTheme.Dark : ApplicationTheme.Light;

            // WPF-UI rebuilds control styles when the theme dictionary changes, capturing the
            // accent brushes at that moment. Update the accent first so buttons keep the right
            // background and text colors after a runtime theme switch.
            ApplicationAccentColorManager.Apply(AccentColor, theme, systemGlassColor: false, systemAccentColor: false);
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);

            IsDark = isDark;
            if (persist)
            {
                _store.Save(_preferences);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The {Theme} theme could not be applied.", themeName);
        }
    }
}
