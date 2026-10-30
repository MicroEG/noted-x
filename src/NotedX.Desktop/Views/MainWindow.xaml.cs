using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using NotedX.Desktop.Services;
using NotedX.Desktop.ViewModels;

namespace NotedX.Desktop.Views;

/// <summary>Main application window.</summary>
public partial class MainWindow
{
    private readonly MainViewModel _viewModel;
    private readonly TrayService _tray;
    private readonly ILogger<MainWindow> _logger;
    private bool _trayHintShown;

    /// <summary>Creates the shell window with its injected view model.</summary>
    public MainWindow(MainViewModel viewModel, TrayService tray, ILogger<MainWindow> logger)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>Restores the window from the notification area.</summary>
    public void ShowFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The workspace failed to initialize.");
            _viewModel.Toasts.ShowError("The workspace could not be loaded completely. Check the log for details.");
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (App.IsShuttingDown)
        {
            _viewModel.FlushEditor();
            return;
        }

        e.Cancel = true;
        Hide();
        _viewModel.FlushEditor();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.NotifyStillRunning();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (control && e.Key == Key.K)
        {
            e.Handled = true;
            _viewModel.OpenSearchCommand.Execute(null);
            return;
        }

        if (control && e.Key == Key.N)
        {
            e.Handled = true;
            _viewModel.NewPageCommand.Execute(null);
            return;
        }

        if (control && e.Key == Key.S)
        {
            e.Handled = true;
            _viewModel.SaveNowCommand.Execute(null);
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel.IsSettingsOpen)
        {
            _viewModel.CloseSettingsCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.IsPairingOpen)
        {
            _viewModel.ClosePairingCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.IsSearchOpen)
        {
            _viewModel.CloseSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.IsTransfersOpen)
        {
            _viewModel.ToggleTransfersCommand.Execute(null);
            e.Handled = true;
        }
    }
}
