using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NotedX.Desktop.Views;

namespace NotedX.Desktop.Services;

/// <summary>Thin wrapper around the common Windows dialogs used by view models.</summary>
public sealed class DesktopDialogService
{
    private readonly ILogger<DesktopDialogService> _logger;

    /// <summary>Creates the service.</summary>
    public DesktopDialogService(ILogger<DesktopDialogService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Lets the user choose an existing file.</summary>
    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    /// <summary>Lets the user choose where to save a file.</summary>
    public string? PickSaveFile(string title, string filter, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedName,
            AddExtension = true,
            OverwritePrompt = true
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    /// <summary>Asks a yes/no question and defaults to "no".</summary>
    /// <param name="title">Question shown as the dialog heading.</param>
    /// <param name="message">Explanation of what the confirmation does.</param>
    /// <param name="confirmText">Label on the affirmative button.</param>
    /// <param name="isDestructive">Styles the affirmative button as a destructive action.</param>
    public bool Confirm(string title, string message, string confirmText = "Yes", bool isDestructive = false)
    {
        try
        {
            var owner = Application.Current?.MainWindow;
            var hasOwner = owner is { IsVisible: true };

            var dialog = new ConfirmWindow(title, message, confirmText, isDestructive)
            {
                Owner = hasOwner ? owner : null,
                WindowStartupLocation = hasOwner
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen
            };

            return dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The confirmation dialog for {Title} could not be shown.", title);
            return false;
        }
    }

    /// <summary>Opens a directory in File Explorer.</summary>
    public bool OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the folder {FolderPath}.", path);
            return false;
        }
    }
}
