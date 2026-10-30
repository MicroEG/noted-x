using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotedX.Core.Domain;
using NotedX.Core.Notes;
using NotedX.Desktop.Services;

namespace NotedX.Desktop.ViewModels;

/// <summary>Lists every file stored by the workspace.</summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private readonly AttachmentService _attachments;
    private readonly AttachmentPreviewService _previews;
    private readonly DesktopDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ILogger<FilesViewModel> _logger;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Creates the file browser.</summary>
    public FilesViewModel(
        AttachmentService attachments,
        AttachmentPreviewService previews,
        DesktopDialogService dialogs,
        ToastService toasts,
        ILogger<FilesViewModel> logger)
    {
        _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
        _previews = previews ?? throw new ArgumentNullException(nameof(previews));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised when the user asks to open the note that owns a file.</summary>
    public event Action<Guid>? PageRequested;

    /// <summary>Stored files, newest first.</summary>
    public ObservableCollection<AttachmentItemViewModel> Items { get; } = new();

    /// <summary>Whether no files are stored.</summary>
    public bool IsEmpty => !IsLoading && Items.Count == 0;

    /// <summary>Reloads the file list.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;

        try
        {
            var attachments = await _attachments.ListAllAsync();
            Items.Clear();
            foreach (var attachment in attachments)
            {
                Items.Add(new AttachmentItemViewModel(attachment));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading stored files failed.");
            _toasts.ShowError("The file list could not be loaded.");
        }
        finally
        {
            IsLoading = false;
            NotifyEmptyChanged();
        }
    }

    /// <summary>Opens a stored file with its default application.</summary>
    [RelayCommand]
    private async Task OpenAsync(AttachmentItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _previews.OpenAsync(item.Attachment);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening stored file {AttachmentId} failed.", item.Attachment.Id);
            _toasts.ShowError("The file could not be opened. It may have been removed from the store.");
        }
    }

    /// <summary>Opens the note that owns the file.</summary>
    [RelayCommand]
    private void OpenPage(AttachmentItemViewModel? item)
    {
        if (item?.Attachment.PageId is { } pageId)
        {
            PageRequested?.Invoke(pageId);
        }
    }

    /// <summary>Removes the file record from the workspace.</summary>
    [RelayCommand]
    private async Task DeleteAsync(AttachmentItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Remove this file?",
                $"“{item.FileName}” will disappear from the file list and from notes that reference it. The stored content is shared and stays on disk.",
                "Remove file",
                isDestructive: true))
        {
            return;
        }

        try
        {
            var removed = await _attachments.DeleteAsync(item.Attachment.Id);
            if (!removed)
            {
                _toasts.ShowWarning("The file was already removed.");
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing attachment {AttachmentId} failed.", item.Attachment.Id);
            _toasts.ShowError("The file could not be removed.");
        }
    }

    private void NotifyEmptyChanged() => OnPropertyChanged(nameof(IsEmpty));
}

/// <summary>A stored file formatted for the file browser.</summary>
public sealed class AttachmentItemViewModel
{
    /// <summary>Creates the item from a stored attachment.</summary>
    public AttachmentItemViewModel(Attachment attachment)
    {
        Attachment = attachment ?? throw new ArgumentNullException(nameof(attachment));
        FileName = string.IsNullOrWhiteSpace(attachment.FileName) ? "Unnamed file" : attachment.FileName;
        SizeText = ByteFormatter.FormatSize(attachment.SizeBytes);
        AddedText = $"{attachment.CreatedAt.ToLocalTime():MMM d, yyyy} · {attachment.UploadedBy}";
        Icon = IconFor(attachment.FileName, attachment.MimeType);
        HasPage = attachment.PageId.HasValue;
    }

    /// <summary>The stored attachment.</summary>
    public Attachment Attachment { get; }

    /// <summary>Display name.</summary>
    public string FileName { get; }

    /// <summary>Human-readable size.</summary>
    public string SizeText { get; }

    /// <summary>When and by whom the file was added.</summary>
    public string AddedText { get; }

    /// <summary>Symbol-ish glyph chosen from the file type.</summary>
    public string Icon { get; }

    /// <summary>Whether the file belongs to a note.</summary>
    public bool HasPage { get; }

    private static string IconFor(string? fileName, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return "🖼";
            }

            if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                return "🎵";
            }

            if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                return "🎬";
            }

            if (mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            {
                return "📕";
            }
        }

        return Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".md" or ".txt" => "📝",
            ".zip" or ".7z" or ".rar" => "🗜",
            ".doc" or ".docx" => "📘",
            ".xls" or ".xlsx" or ".csv" => "📗",
            ".ppt" or ".pptx" => "📙",
            _ => "📎"
        };
    }
}
