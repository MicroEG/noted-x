using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotedX.Core.Storage;
using NotedX.Desktop.Services;
using NotedX.Desktop.Settings;
using NotedX.Server;
using NotedX.Server.Discovery;
using NotedX.Server.Net;

namespace NotedX.Desktop.ViewModels;

/// <summary>Preferences and read-only diagnostics shown in the settings dialog.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly PreferencesStore _store;
    private readonly DesktopPreferences _preferences;
    private readonly ThemeService _theme;
    private readonly NotedXWebServer _server;
    private readonly LanDiscoveryService _discovery;
    private readonly ServerSettings _serverSettings;
    private readonly AppPaths _paths;
    private readonly FirewallService _firewall;
    private readonly DesktopDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ILogger<SettingsViewModel> _logger;

    private bool _disposed;

    [ObservableProperty]
    private bool _requirePairingPin;

    [ObservableProperty]
    private bool _useHttps;

    [ObservableProperty]
    private bool _showRestartHint;

    [ObservableProperty]
    private string _serverAddress = string.Empty;

    [ObservableProperty]
    private string _listeningText = string.Empty;

    [ObservableProperty]
    private string _addressesText = string.Empty;

    [ObservableProperty]
    private string _discoveryText = string.Empty;

    [ObservableProperty]
    private string _dataFolder = string.Empty;

    [ObservableProperty]
    private string _maxFileText = string.Empty;

    [ObservableProperty]
    private string _firewallStatus = string.Empty;

    /// <summary>Creates the settings panel.</summary>
    public SettingsViewModel(
        PreferencesStore store,
        DesktopPreferences preferences,
        ThemeService theme,
        NotedXWebServer server,
        LanDiscoveryService discovery,
        ServerSettings serverSettings,
        AppPaths paths,
        FirewallService firewall,
        DesktopDialogService dialogs,
        ToastService toasts,
        ILogger<SettingsViewModel> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _serverSettings = serverSettings ?? throw new ArgumentNullException(nameof(serverSettings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _firewall = firewall ?? throw new ArgumentNullException(nameof(firewall));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _requirePairingPin = _preferences.RequirePairingPin;
        _useHttps = _preferences.UseHttps;

        _theme.Changed += OnThemeChanged;
        Refresh();
    }

    /// <summary>Whether dark mode is active. Toggling applies the theme immediately.</summary>
    public bool IsDark
    {
        get => _theme.IsDark;
        set
        {
            if (value == _theme.IsDark)
            {
                return;
            }

            _theme.SetTheme(value ? "Dark" : "Light");
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeLabel));
        }
    }

    /// <summary>Name of the active theme.</summary>
    public string ThemeLabel => IsDark ? "Dark" : "Light";

    /// <summary>Explanation of the current PIN requirement.</summary>
    public string PinStatusText => RequirePairingPin
        ? "Every pairing code also requires the four-digit PIN shown on this PC."
        : "Anyone on your network who has an active code can pair a device.";

    /// <summary>Explanation of the HTTPS option.</summary>
    public string HttpsStatusText => _preferences.UseHttps
        ? "Phones must trust a self-signed certificate, so browsers will show a warning."
        : "Traffic between this PC and paired devices is unencrypted on the local network.";

    /// <summary>Re-reads server and storage details for the dialog.</summary>
    public void Refresh()
    {
        try
        {
            ServerAddress = _server.BaseUrl ?? "The local server is not running";
            ListeningText = _server.IsRunning
                ? $"Listening on port {_server.Port}"
                : "The local server is not running";

            var addresses = LanAddressProvider.GetAddresses();
            AddressesText = addresses.Count == 0
                ? "No local network address was detected"
                : string.Join("   ·   ", addresses.Select(address => address.Address));

            DiscoveryText = $"mDNS {(_serverSettings.EnableMdns ? "on" : "off")} · " +
                            $"UDP discovery {(_discovery.UdpPort?.ToString() ?? "off")}";

            DataFolder = _paths.Root;
            MaxFileText = ByteFormatter.FormatSize(_serverSettings.MaxUploadBytes);
            ShowRestartHint = _preferences.UseHttps != _server.UsesHttps;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Server details could not be refreshed.");
        }
    }

    /// <summary>Opens the application data folder.</summary>
    [RelayCommand]
    private void OpenDataFolder()
    {
        if (!_dialogs.OpenFolder(_paths.Root))
        {
            _toasts.ShowWarning("The data folder could not be opened.");
        }
    }

    /// <summary>Copies the LAN address to the clipboard.</summary>
    [RelayCommand]
    private void CopyAddress()
    {
        if (_server.BaseUrl is not { } address)
        {
            _toasts.ShowWarning("The local server is not running.");
            return;
        }

        try
        {
            Clipboard.SetText(address);
            _toasts.ShowSuccess("Paste it next to the pairing code.", "Server address copied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The server address could not be copied to the clipboard.");
            _toasts.ShowError("The clipboard is busy. Try again in a moment.");
        }
    }

    /// <summary>Adds private-network firewall rules after an elevation prompt.</summary>
    [RelayCommand]
    private async Task AllowFirewallAsync()
    {
        if (!_server.IsRunning || _server.Port is < 1 or > 65535)
        {
            _toasts.ShowWarning("Start the local server before changing firewall rules.");
            return;
        }

        FirewallStatus = "Waiting for Windows to confirm…";

        try
        {
            var result = await _firewall.EnsureRulesAsync(
                _server.Port,
                _discovery.UdpPort ?? _serverSettings.DiscoveryPort);

            FirewallStatus = result switch
            {
                FirewallRuleResult.Succeeded => "Inbound rules are in place for private networks.",
                FirewallRuleResult.Cancelled => "No changes were made.",
                FirewallRuleResult.Unsupported => "Automatic firewall rules are not available.",
                _ => "The firewall rules could not be added."
            };

            if (result == FirewallRuleResult.Succeeded)
            {
                _toasts.ShowSuccess("Phones on your Wi-Fi can now reach this PC.", "Firewall rules added");
            }
            else if (result == FirewallRuleResult.Failed)
            {
                _toasts.ShowWarning("Allow noted-x through Windows Firewall manually if phones cannot connect.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adding firewall rules failed.");
            FirewallStatus = "The firewall rules could not be added.";
            _toasts.ShowError("The firewall rules could not be added.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _theme.Changed -= OnThemeChanged;
    }

    partial void OnRequirePairingPinChanged(bool value)
    {
        _preferences.RequirePairingPin = value;
        _serverSettings.RequirePairingPin = value;
        SavePreferences();
        OnPropertyChanged(nameof(PinStatusText));
    }

    partial void OnUseHttpsChanged(bool value)
    {
        _preferences.UseHttps = value;
        SavePreferences();
        ShowRestartHint = _preferences.UseHttps != _server.UsesHttps;
        OnPropertyChanged(nameof(HttpsStatusText));
    }

    private void SavePreferences()
    {
        if (!_store.Save(_preferences))
        {
            _toasts.ShowWarning("The preference was applied for this session but could not be saved.");
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(ThemeLabel));
    }
}
