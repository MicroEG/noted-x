using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace NotedX.Desktop.Services;

/// <summary>Provides notification-area shortcuts while the app keeps running in the background.</summary>
public sealed class TrayService : IDisposable
{
    private readonly ILogger<TrayService> _logger;

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _menu;
    private System.Drawing.Icon? _icon;
    private bool _initialized;

    /// <summary>Creates the service.</summary>
    public TrayService(ILogger<TrayService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Creates the tray icon and its quick actions.</summary>
    public void Initialize(Action showWindow, Action pairDevice, Action openSettings, Action quit)
    {
        if (_initialized || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _menu = new Forms.ContextMenuStrip
            {
                ShowImageMargin = false
            };

            _menu.Items.Add(CreateItem("Open noted-x", showWindow));
            _menu.Items.Add(CreateItem("Pair a phone", pairDevice));
            _menu.Items.Add(CreateItem("Settings", openSettings));
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add(CreateItem("Quit", quit));

            _icon = LoadApplicationIcon();

            _notifyIcon = new Forms.NotifyIcon
            {
                Text = "noted-x",
                Icon = _icon,
                ContextMenuStrip = _menu,
                Visible = true
            };

            _notifyIcon.DoubleClick += (_, _) => showWindow();
            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The notification area icon could not be created.");
        }
    }

    /// <summary>Shows a one-time balloon explaining that the app is still running.</summary>
    public void NotifyStillRunning()
    {
        try
        {
            if (_notifyIcon is null)
            {
                return;
            }

            _notifyIcon.BalloonTipTitle = "noted-x is still running";
            _notifyIcon.BalloonTipText = "Your notes stay available to paired devices. Use the tray icon to reopen the app.";
            _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The notification area balloon could not be shown.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _menu?.Dispose();
            _menu = null;
            _icon?.Dispose();
            _icon = null;
            _initialized = false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The notification area icon could not be disposed.");
        }
    }

    private static Forms.ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch
            {
                // The callbacks marshal to the WPF dispatcher and log their own failures.
            }
        };

        return item;
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // Fall through to the generic system icon.
        }

        return System.Drawing.SystemIcons.Application;
    }
}
