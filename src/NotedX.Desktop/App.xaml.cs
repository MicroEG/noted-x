using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NotedX.Core.Storage;
using NotedX.Desktop.Services;
using NotedX.Desktop.Settings;
using NotedX.Desktop.ViewModels;
using NotedX.Desktop.Views;
using NotedX.Server;
using NotedX.Server.Discovery;
using Serilog;

namespace NotedX.Desktop;

/// <summary>Application entry point and composition root.</summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayService? _tray;
    private MainViewModel? _viewModel;

    /// <summary>Whether the process is on its way out and close-to-tray should be skipped.</summary>
    public static bool IsShuttingDown { get; private set; }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.CreateDefault();

        try
        {
            paths.EnsureDirectories();
            ConfigureLogging(paths);
            RegisterCrashHandlers();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "noted-x could not prepare its workspace.\n\n" + ex.Message,
                "noted-x",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        try
        {
            var preferencesStore = new PreferencesStore(paths, NullLogger<PreferencesStore>.Instance);
            var preferences = preferencesStore.Load();
            var serverSettings = new ServerSettings
            {
                UseHttps = preferences.UseHttps,
                RequirePairingPin = preferences.RequirePairingPin
            };

            _host = Host.CreateDefaultBuilder()
                .UseSerilog(Log.Logger, dispose: false)
                .ConfigureServices(services =>
                {
                    services.AddSingleton(paths);
                    services.AddSingleton(preferencesStore);
                    services.AddSingleton(preferences);
                    services.AddSingleton(serverSettings);
                    services.AddSingleton(TimeProvider.System);

                    services.AddSingleton<NotedXWebServer>();
                    services.AddHostedService(provider => provider.GetRequiredService<NotedXWebServer>());

                    services.AddSingleton<LanDiscoveryService>();
                    services.AddHostedService(provider => provider.GetRequiredService<LanDiscoveryService>());

                    services.AddSingleton<ToastService>();
                    services.AddSingleton<ThemeService>();
                    services.AddSingleton<FirewallService>();
                    services.AddSingleton<DesktopDialogService>();
                    services.AddSingleton<TrayService>();

                    services.AddSingleton(provider => new AttachmentPreviewService(
                        provider.GetRequiredService<NotedXWebServer>().Attachments,
                        provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AttachmentPreviewService>>()));

                    services.AddSingleton(provider => new PageEditorViewModel(
                        provider.GetRequiredService<NotedXWebServer>().Pages,
                        provider.GetRequiredService<NotedXWebServer>().Attachments,
                        provider.GetRequiredService<AttachmentPreviewService>(),
                        provider.GetRequiredService<DesktopDialogService>(),
                        provider.GetRequiredService<ToastService>(),
                        provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PageEditorViewModel>>()));

                    services.AddSingleton(provider => new SearchViewModel(
                        provider.GetRequiredService<NotedXWebServer>().Pages,
                        provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SearchViewModel>>()));

                    services.AddSingleton(provider => new FilesViewModel(
                        provider.GetRequiredService<NotedXWebServer>().Attachments,
                        provider.GetRequiredService<AttachmentPreviewService>(),
                        provider.GetRequiredService<DesktopDialogService>(),
                        provider.GetRequiredService<ToastService>(),
                        provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FilesViewModel>>()));

                    services.AddSingleton<TransfersViewModel>();
                    services.AddSingleton<PairingViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            _host.Start();

            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
            Log.Information("noted-x desktop {Version} started", version);

            _host.Services.GetRequiredService<ThemeService>().Initialize();

            _viewModel = _host.Services.GetRequiredService<MainViewModel>();
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            _tray = _host.Services.GetRequiredService<TrayService>();
            _tray.Initialize(
                () => Dispatcher.BeginInvoke(window.ShowFromTray),
                () => Dispatcher.BeginInvoke(() =>
                {
                    window.ShowFromTray();
                    _viewModel.OpenPairingCommand.Execute(null);
                }),
                () => Dispatcher.BeginInvoke(() =>
                {
                    window.ShowFromTray();
                    _viewModel.OpenSettingsCommand.Execute(null);
                }),
                () => Dispatcher.BeginInvoke(() => _ = ShutdownFromTrayAsync()));
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The application failed to start");
            MessageBox.Show(
                "noted-x could not start.\n\n" + ex.Message,
                "noted-x",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <inheritdoc />
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsShuttingDown = true;

        try
        {
            _viewModel?.FlushEditor();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The editor could not be flushed while Windows was shutting down");
        }

        base.OnSessionEnding(e);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "The notification area icon could not be disposed");
        }

        try
        {
            if (_host is not null)
            {
                _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The application host failed to stop cleanly");
        }
        finally
        {
            _host?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    private async Task ShutdownFromTrayAsync()
    {
        if (IsShuttingDown)
        {
            return;
        }

        IsShuttingDown = true;

        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.PrepareShutdownAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving before shutdown failed");
        }

        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "The notification area icon could not be disposed");
        }

        Shutdown();
    }

    private static void ConfigureLogging(AppPaths paths)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "notedx-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(2))
            .CreateLogger();
    }

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled user interface exception");
        MessageBox.Show(
            "Something unexpected happened. Details were written to the log.",
            "noted-x",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled application domain exception");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved background task exception");
        e.SetObserved();
    }
}
