using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotedX.Desktop.Services;
using NotedX.Server;
using NotedX.Server.Discovery;
using NotedX.Server.Net;
using NotedX.Server.Security;

namespace NotedX.Desktop.ViewModels;

/// <summary>Creates short-lived pairing codes and manages the devices paired with this PC.</summary>
public sealed partial class PairingViewModel : ObservableObject, IDisposable
{
    private readonly NotedXWebServer _server;
    private readonly LanDiscoveryService _discovery;
    private readonly ServerSettings _settings;
    private readonly FirewallService _firewall;
    private readonly DesktopDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PairingViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private DateTimeOffset _expiresAt;
    private bool _disposed;

    [ObservableProperty]
    private string _link = string.Empty;

    [ObservableProperty]
    private BitmapImage? _qrCode;

    [ObservableProperty]
    private string _pin = string.Empty;

    [ObservableProperty]
    private bool _hasPin;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    [ObservableProperty]
    private bool _isExpired;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasActiveSession;

    [ObservableProperty]
    private bool _isLoadingDevices;

    [ObservableProperty]
    private string _addressText = string.Empty;

    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private bool _hasWarning;

    [ObservableProperty]
    private string _firewallStatus = string.Empty;

    /// <summary>Creates the pairing panel over the running local server.</summary>
    public PairingViewModel(
        NotedXWebServer server,
        LanDiscoveryService discovery,
        ServerSettings settings,
        FirewallService firewall,
        DesktopDialogService dialogs,
        ToastService toasts,
        TimeProvider timeProvider,
        ILogger<PairingViewModel> logger)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _firewall = firewall ?? throw new ArgumentNullException(nameof(firewall));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _timer.Tick += OnTimerTick;
        _server.Pairing.DevicePaired += OnDevicePaired;

        AddressText = _server.BaseUrl ?? "The local server is not running.";
        LoadNetworkWarning();
    }

    /// <summary>Devices that have redeemed a pairing code.</summary>
    public ObservableCollection<PairedDeviceItemViewModel> Devices { get; } = new();

    /// <summary>Whether any device has been paired.</summary>
    public bool HasDevices => Devices.Count > 0;

    /// <summary>Refreshes the code and the device list when the panel opens.</summary>
    public async Task PrepareAsync()
    {
        Generate();
        await RefreshDevicesAsync();
    }

    /// <summary>Creates a fresh pairing code, replacing any previous one.</summary>
    [RelayCommand]
    private void Generate()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var session = _server.Pairing.CreateSession();
            var uri = _server.BuildPairingUri(session);

            Link = uri.AbsoluteUri;
            Pin = session.Pin ?? string.Empty;
            HasPin = !string.IsNullOrEmpty(Pin);
            QrCode = PairingQrCode.Create(Link);
            _expiresAt = session.ExpiresAt;
            HasActiveSession = true;
            IsExpired = false;
            UpdateRemainingText();
            _timer.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A pairing code could not be created.");
            HasActiveSession = false;
            IsExpired = false;
            QrCode = null;
            Link = string.Empty;
            Pin = string.Empty;
            HasPin = false;
            RemainingText = "Pairing is unavailable";
            _toasts.ShowError("Make sure the local server is running, then try again.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Copies the pairing link to the clipboard.</summary>
    [RelayCommand]
    private void CopyLink()
    {
        if (!HasActiveSession || string.IsNullOrWhiteSpace(Link))
        {
            _toasts.ShowWarning("Generate a fresh pairing code first.");
            return;
        }

        try
        {
            Clipboard.SetText(Link);
            _toasts.ShowSuccess("Open it in the phone's browser or paste it by hand.", "Pairing link copied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The pairing link could not be copied to the clipboard.");
            _toasts.ShowError("The clipboard is busy. Try again in a moment.");
        }
    }

    /// <summary>Reloads the paired device list.</summary>
    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsLoadingDevices = true;

        try
        {
            var devices = await _server.Devices.ListAsync();
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(new PairedDeviceItemViewModel(device));
            }

            OnPropertyChanged(nameof(HasDevices));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The paired device list could not be loaded.");
        }
        finally
        {
            IsLoadingDevices = false;
        }
    }

    /// <summary>Revokes a device token.</summary>
    [RelayCommand]
    private async Task RevokeAsync(PairedDeviceItemViewModel? device)
    {
        if (device is null || device.IsRevoked)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Revoke this device?",
                $"“{device.Name}” will lose access immediately. Pair it again to restore access.",
                "Revoke",
                isDestructive: true))
        {
            return;
        }

        try
        {
            await _server.Devices.RevokeAsync(device.Id);
            _toasts.ShowSuccess($"{device.Name} can no longer open your notes.", "Device revoked");
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Revoking device {DeviceId} failed.", device.Id);
            _toasts.ShowError("The device could not be revoked.");
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
                _discovery.UdpPort ?? _settings.DiscoveryPort);

            FirewallStatus = result switch
            {
                FirewallRuleResult.Succeeded => "Windows Firewall now allows noted-x on private networks.",
                FirewallRuleResult.Cancelled => "No changes were made.",
                FirewallRuleResult.Unsupported => "Automatic firewall rules are not available on this platform.",
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
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _server.Pairing.DevicePaired -= OnDevicePaired;
        QrCode = null;
    }

    private void OnDevicePaired(object? sender, DevicePairedEventArgs e) =>
        Dispatch(() =>
        {
            _timer.Stop();
            HasActiveSession = false;
            IsExpired = true;
            RemainingText = "Device paired";
            Pin = string.Empty;
            HasPin = false;
            QrCode = null;
            Link = string.Empty;

            _toasts.ShowSuccess(
                $"{e.Device.Name} can now open your notes on this network.",
                "Device paired");

            _ = RefreshDevicesAsync();
        });

    private void OnTimerTick(object? sender, EventArgs e) => UpdateRemainingText();

    private void UpdateRemainingText()
    {
        if (!HasActiveSession)
        {
            _timer.Stop();
            return;
        }

        var remaining = _expiresAt - _timeProvider.GetUtcNow();

        if (remaining <= TimeSpan.Zero)
        {
            ExpireCode();
            return;
        }

        RemainingText = remaining.TotalMinutes >= 1
            ? $"Expires in {(int)remaining.TotalMinutes}:{remaining.Seconds:00}"
            : $"Expires in {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}s";
    }

    private void ExpireCode()
    {
        _timer.Stop();
        HasActiveSession = false;
        IsExpired = true;
        RemainingText = "This code expired";
        Pin = string.Empty;
        HasPin = false;
        QrCode = null;
        Link = string.Empty;
    }

    private void LoadNetworkWarning()
    {
        try
        {
            var publicAddresses = LanAddressProvider.GetAddresses()
                .Where(address => !address.IsPrivate)
                .ToArray();

            HasWarning = publicAddresses.Length > 0;
            WarningText = HasWarning
                ? "This computer has a public network address. Keep Windows Firewall enabled so noted-x stays on your local network."
                : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Network addresses could not be inspected for the pairing warning.");
        }
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>A paired mobile device shown in the pairing panel.</summary>
public sealed class PairedDeviceItemViewModel
{
    /// <summary>Creates the item from a stored device.</summary>
    public PairedDeviceItemViewModel(PairedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        Id = device.Id;
        Name = string.IsNullOrWhiteSpace(device.Name) ? "Mobile device" : device.Name.Trim();
        IsRevoked = device.IsRevoked;
        Icon = "📱";

        var paired = device.CreatedAt.ToLocalTime().ToString("MMM d, yyyy");
        DetailsText = device.IsRevoked
            ? $"Revoked · paired {paired}"
            : device.LastSeenAt is { } seen
                ? $"Last seen {FormatRelative(seen)} · paired {paired}"
                : $"Paired {paired}";

        StatusText = device.IsRevoked ? "Revoked" : "Active";
    }

    /// <summary>Device identifier.</summary>
    public Guid Id { get; }

    /// <summary>Friendly device name.</summary>
    public string Name { get; }

    /// <summary>Display glyph for the device row.</summary>
    public string Icon { get; }

    /// <summary>Pairing and last-seen summary.</summary>
    public string DetailsText { get; }

    /// <summary>Short status label.</summary>
    public string StatusText { get; }

    /// <summary>Whether the device token has been revoked.</summary>
    public bool IsRevoked { get; }

    private static string FormatRelative(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.Now - timestamp.ToLocalTime();

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)}h ago";
        }

        return timestamp.ToLocalTime().ToString("MMM d");
    }
}
