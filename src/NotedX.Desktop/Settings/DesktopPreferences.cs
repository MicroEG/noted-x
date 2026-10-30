namespace NotedX.Desktop.Settings;

/// <summary>Preferences that survive application restarts.</summary>
public sealed class DesktopPreferences
{
    /// <summary>Selected color theme: <c>Light</c> or <c>Dark</c>.</summary>
    public string Theme { get; set; } = "Light";

    /// <summary>Whether the local server should use HTTPS on the next start.</summary>
    public bool UseHttps { get; set; }

    /// <summary>Whether pairing a phone also requires the four-digit PIN.</summary>
    public bool RequirePairingPin { get; set; } = true;

    /// <summary>Whether the navigation sidebar starts collapsed.</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>Returns a valid theme name.</summary>
    public string NormalizedTheme() =>
        string.Equals(Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
}
