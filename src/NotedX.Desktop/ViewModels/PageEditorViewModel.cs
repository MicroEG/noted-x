using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotedX.Core.Domain;
using NotedX.Core.Notes;
using NotedX.Desktop.Services;

namespace NotedX.Desktop.ViewModels;

/// <summary>Coordinates the title, blocks, tags, history, and autosave of the open page.</summary>
public sealed partial class PageEditorViewModel : ObservableObject, IDisposable
{
    private const int AutosaveDelayMilliseconds = 500;
    private const int MaximumTags = 20;

    private static readonly SlashCommandItem[] SlashCatalog =
    [
        new("Text", "Plain paragraph", "¶", BlockType.Paragraph, SlashCommandAction.Convert),
        new("Heading 1", "Large section title", "H1", BlockType.Heading1, SlashCommandAction.Convert),
        new("Heading 2", "Medium section title", "H2", BlockType.Heading2, SlashCommandAction.Convert),
        new("Heading 3", "Small section title", "H3", BlockType.Heading3, SlashCommandAction.Convert),
        new("Bulleted list", "Simple bullet list", "•", BlockType.BulletedList, SlashCommandAction.Convert),
        new("Numbered list", "Ordered list", "1.", BlockType.NumberedList, SlashCommandAction.Convert),
        new("To-do", "Checklist item", "☑", BlockType.ToDo, SlashCommandAction.Convert),
        new("Quote", "Highlighted quote", "❝", BlockType.Quote, SlashCommandAction.Convert),
        new("Code", "Monospaced code block", "{}", BlockType.Code, SlashCommandAction.Convert),
        new("Divider", "Horizontal separator", "—", BlockType.Divider, SlashCommandAction.Convert),
        new("Table", "Simple editable table", "▦", BlockType.Table, SlashCommandAction.Convert),
        new("Image", "Embed a local image", "🖼", BlockType.Image, SlashCommandAction.Image),
        new("File", "Attach a file", "📎", BlockType.File, SlashCommandAction.File)
    ];

    private readonly PageService _pages;
    private readonly AttachmentService _attachments;
    private readonly AttachmentPreviewService _previews;
    private readonly DesktopDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ILogger<PageEditorViewModel> _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private CancellationTokenSource? _saveDebounce;
    private int _loadVersion;
    private bool _isLoading;
    private bool _suppressDirty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    [ObservableProperty]
    private string _tagInput = string.Empty;

    [ObservableProperty]
    private bool _isHistoryOpen;

    [ObservableProperty]
    private bool _isIconPickerOpen;

    /// <summary>Creates the editor over the shared page and attachment services.</summary>
    public PageEditorViewModel(
        PageService pages,
        AttachmentService attachments,
        AttachmentPreviewService previews,
        DesktopDialogService dialogs,
        ToastService toasts,
        ILogger<PageEditorViewModel> logger)
    {
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
        _previews = previews ?? throw new ArgumentNullException(nameof(previews));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised after the page was saved successfully.</summary>
    public event EventHandler<PageSavedEventArgs>? Saved;

    /// <summary>Raised when the user asks to move the page to the trash.</summary>
    public event EventHandler? DeleteRequested;

    /// <summary>Identifier of the open page, or <see cref="Guid.Empty"/>.</summary>
    public Guid PageId { get; private set; }

    /// <summary>Editable blocks in display order.</summary>
    public ObservableCollection<BlockEditorViewModel> Blocks { get; } = new();

    /// <summary>Tags applied to the page.</summary>
    public ObservableCollection<string> Tags { get; } = new();

    /// <summary>Saved versions, newest first.</summary>
    public ObservableCollection<RevisionItemViewModel> Revisions { get; } = new();

    /// <summary>Emoji choices offered by the icon picker.</summary>
    public IReadOnlyList<string> IconChoices { get; } =
    [
        "📄", "📝", "📌", "💡", "🎯", "📚", "🧪", "🛠️", "🌱", "🚀", "🗂️", "🧭",
        "🎨", "🎵", "📷", "🧠", "☕", "🔥", "⭐", "✅", "🏡", "💼", "🧾", "🔒",
        "🌍", "🧩", "📊", "✈️"
    ];

    /// <summary>Loads a page into the editor, saving any pending changes from the previous page.</summary>
    public async Task OpenAsync(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (IsLoaded && page.Id == PageId)
        {
            await ReloadFromServerAsync();
            return;
        }

        if (!await FlushAsync())
        {
            return;
        }

        _loadVersion++;
        Populate(page);
        await LoadRevisionsAsync();
        await HydrateAttachmentsAsync();
    }

    /// <summary>Re-reads the open page from the database, unless there are unsaved edits.</summary>
    public async Task ReloadFromServerAsync()
    {
        if (!IsLoaded || IsDirty || IsSaving)
        {
            return;
        }

        var page = await _pages.GetAsync(PageId);
        if (page is null)
        {
            return;
        }

        _loadVersion++;
        Populate(page);
        await LoadRevisionsAsync();
        await HydrateAttachmentsAsync();
    }

    /// <summary>Saves pending edits immediately and reports whether they are safe on disk.</summary>
    public async Task<bool> FlushAsync()
    {
        CancelScheduledSave();
        if (IsLoaded && IsDirty)
        {
            return await SaveAsync();
        }

        return true;
    }

    /// <summary>Clears the editor when the page is closed or deleted.</summary>
    public void Close()
    {
        CancelScheduledSave();
        _loadVersion++;
        _isLoading = true;

        try
        {
            foreach (var block in Blocks)
            {
                block.PropertyChanged -= OnBlockPropertyChanged;
            }

            Blocks.Clear();
            Tags.Clear();
            Revisions.Clear();
            PageId = Guid.Empty;
            Title = string.Empty;
            Icon = string.Empty;
            IsFavorite = false;
            IsLoaded = false;
            IsDirty = false;
            IsSaving = false;
            SaveStatus = string.Empty;
            TagInput = string.Empty;
            IsHistoryOpen = false;
            IsIconPickerOpen = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Persists the current content and returns whether the page is fully saved.</summary>
    public async Task<bool> SaveAsync()
    {
        if (!IsLoaded || !IsDirty)
        {
            return true;
        }

        await _saveGate.WaitAsync();
        var failed = false;

        try
        {
            if (!IsDirty)
            {
                return true;
            }

            CancelScheduledSave();
            IsSaving = true;
            IsDirty = false;
            SaveStatus = "Saving…";

            var drafts = BuildDrafts();
            var title = NormalizeTitle(Title);
            var icon = string.IsNullOrWhiteSpace(Icon) ? string.Empty : Icon;

            Page? updated;
            try
            {
                updated = await _pages.UpdateAsync(
                    PageId,
                    new PageChanges(title, icon, null, drafts, null));
            }
            catch (Exception ex)
            {
                failed = true;
                IsDirty = true;
                SaveStatus = "Couldn't save";
                _logger.LogError(ex, "Saving page {PageId} failed.", PageId);
                _toasts.ShowError(
                    "Your changes are still in the editor. Check the log for details.",
                    "The note could not be saved");
                return false;
            }

            if (updated is null)
            {
                failed = true;
                SaveStatus = "This note no longer exists";
                _toasts.ShowWarning("The note was deleted or moved to the trash on another device.");
                return false;
            }

            _suppressDirty = true;
            try
            {
                if (!string.Equals(Title, updated.Title, StringComparison.Ordinal))
                {
                    Title = updated.Title;
                }

                if (!string.Equals(Icon, updated.Icon ?? string.Empty, StringComparison.Ordinal))
                {
                    Icon = updated.Icon ?? string.Empty;
                }
            }
            finally
            {
                _suppressDirty = false;
            }

            SaveStatus = "Saved " + updated.UpdatedAt.ToLocalTime().ToString("t");
            Saved?.Invoke(
                this,
                new PageSavedEventArgs(updated.Id, updated.Title, updated.Icon, updated.IsFavorite, updated.UpdatedAt));
            return true;
        }
        finally
        {
            IsSaving = false;
            _saveGate.Release();

            if (!failed && IsDirty && IsLoaded)
            {
                ScheduleSave();
            }
        }
    }

    /// <summary>Marks block content as changed and schedules the debounced save.</summary>
    public void MarkContentDirty() => MarkDirty();

    /// <summary>Handles text changes, markdown prefixes, and slash-menu filtering.</summary>
    public void HandleTextChanged(BlockEditorViewModel block, string text, int caret)
    {
        if (_isLoading || !IsLoaded)
        {
            return;
        }

        if (TryApplyMarkdownShortcut(block, text))
        {
            return;
        }

        UpdateSlashMenu(block, text);
    }

    /// <summary>Splits a block at the caret or picks the highlighted slash command.</summary>
    public async Task HandleEnterAsync(BlockEditorViewModel block, int caret)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (block.IsSlashMenuOpen && block.SlashItems.Count > 0)
        {
            await ApplySlashCommandAsync(block, block.SlashItems[0]);
            return;
        }

        var index = Blocks.IndexOf(block);
        if (index < 0)
        {
            return;
        }

        if (!block.IsTextual)
        {
            var paragraph = InsertBlock(index + 1, BlockType.Paragraph);
            MarkDirty();
            paragraph.RequestFocus(0);
            return;
        }

        var text = block.Text ?? string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        var before = text[..caret];
        var after = text[caret..];

        var nextType = block.Type switch
        {
            BlockType.BulletedList => BlockType.BulletedList,
            BlockType.NumberedList => BlockType.NumberedList,
            BlockType.ToDo => BlockType.ToDo,
            BlockType.Quote => BlockType.Quote,
            _ => BlockType.Paragraph
        };

        block.Text = before;
        block.IsSlashMenuOpen = false;
        var next = InsertBlock(index + 1, nextType, after);
        if (nextType == BlockType.ToDo)
        {
            next.IsChecked = false;
        }

        MarkDirty();
        next.RequestFocus(0);
    }

    /// <summary>Merges the block with the previous textual block when backspacing at its start.</summary>
    public bool HandleBackspace(BlockEditorViewModel block)
    {
        if (!IsLoaded)
        {
            return false;
        }

        var index = Blocks.IndexOf(block);
        if (index < 0)
        {
            return false;
        }

        if (index == 0)
        {
            if (block.Type != BlockType.Paragraph && block.Text.Length == 0)
            {
                block.Type = BlockType.Paragraph;
                MarkDirty();
                block.RequestFocus(0);
                return true;
            }

            return false;
        }

        var previous = Blocks[index - 1];
        if (!previous.IsTextual)
        {
            if (block.Text.Length == 0)
            {
                RemoveBlock(block);
                MarkDirty();
            }

            previous.RequestFocus(null);
            return true;
        }

        var merged = previous.Text + block.Text;
        _suppressDirty = true;
        try
        {
            previous.Text = merged;
        }
        finally
        {
            _suppressDirty = false;
        }

        RemoveBlock(block);
        MarkDirty();
        previous.RequestFocus(merged.Length);
        return true;
    }

    /// <summary>Moves focus to the previous or next block at a text boundary.</summary>
    public bool HandleArrowNavigation(BlockEditorViewModel block, bool moveUp)
    {
        var index = Blocks.IndexOf(block);
        if (index < 0)
        {
            return false;
        }

        var targetIndex = moveUp ? index - 1 : index + 1;
        if (targetIndex < 0 || targetIndex >= Blocks.Count)
        {
            return false;
        }

        var target = Blocks[targetIndex];
        target.RequestFocus(moveUp && target.IsTextual ? target.Text.Length : 0);
        return true;
    }

    /// <summary>Runs a slash command against a block.</summary>
    public async Task ApplySlashCommandAsync(BlockEditorViewModel block, SlashCommandItem command)
    {
        if (!IsLoaded || !Blocks.Contains(block))
        {
            return;
        }

        block.IsSlashMenuOpen = false;

        if (command.Action is SlashCommandAction.Image or SlashCommandAction.File)
        {
            var isImage = command.Action == SlashCommandAction.Image;
            var path = _dialogs.PickFile(
                isImage ? "Choose an image" : "Choose a file",
                isImage
                    ? "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff|All files|*.*"
                    : "All files|*.*");

            block.Text = string.Empty;

            if (path is null)
            {
                MarkDirty();
                block.RequestFocus(0);
                return;
            }

            await ImportIntoBlockAsync(block, path, isImage ? BlockType.Image : BlockType.File);

            if (!IsLoaded)
            {
                return;
            }

            var next = InsertBlock(Blocks.IndexOf(block) + 1, BlockType.Paragraph);
            MarkDirty();
            next.RequestFocus(0);
            return;
        }

        block.Text = string.Empty;
        block.Type = command.Type;

        if (command.Type == BlockType.Table)
        {
            block.SetupTable(new[]
            {
                new[] { "Column 1", "Column 2", "Column 3" },
                new[] { string.Empty, string.Empty, string.Empty },
                new[] { string.Empty, string.Empty, string.Empty }
            });
        }
        else if (command.Type == BlockType.Code)
        {
            block.Language = string.Empty;
        }

        MarkDirty();

        if (command.Type is BlockType.Divider or BlockType.Table)
        {
            var next = InsertBlock(Blocks.IndexOf(block) + 1, BlockType.Paragraph);
            MarkDirty();
            next.RequestFocus(0);
        }
        else
        {
            block.RequestFocus(0);
        }
    }

    /// <summary>Opens an image or file block with its default application.</summary>
    public async Task OpenAttachmentAsync(BlockEditorViewModel block)
    {
        if (block.AttachmentId is not { } attachmentId)
        {
            _toasts.ShowInfo("This block has no stored file.");
            return;
        }

        try
        {
            var attachment = await _attachments.GetAsync(attachmentId);
            if (attachment is null)
            {
                _toasts.ShowWarning("The attachment record no longer exists.");
                return;
            }

            await _previews.OpenAsync(attachment);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening attachment for block {BlockId} failed.", block.AttachmentId);
            _toasts.ShowError("The file could not be opened. It may have been removed from the store.");
        }
    }

    /// <summary>Replaces the content of an image block with another local image.</summary>
    public async Task ReplaceImageAsync(BlockEditorViewModel block)
    {
        var path = _dialogs.PickFile(
            "Choose an image",
            "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff|All files|*.*");

        if (path is not null)
        {
            await ImportIntoBlockAsync(block, path, BlockType.Image);
        }
    }

    /// <summary>Converts an image or file block back into an empty paragraph.</summary>
    public void RemoveMedia(BlockEditorViewModel block)
    {
        block.PreviewImage = null;
        block.PreviewError = null;
        block.IsPreviewLoading = false;
        block.AttachmentId = null;
        block.AttachmentName = string.Empty;
        block.AttachmentSizeText = string.Empty;
        block.RemoteUrl = null;
        block.Text = string.Empty;
        block.Type = BlockType.Paragraph;
        block.IsSlashMenuOpen = false;
        MarkDirty();
        block.RequestFocus(0);
    }

    /// <summary>Adds a row to a table block.</summary>
    public void AddTableRow(BlockEditorViewModel block)
    {
        var columns = block.TableRows.Count > 0 ? block.TableRows[0].Cells.Count : 3;
        block.AddTableRow(null, columns);
        MarkDirty();
    }

    /// <summary>Removes a row from a table block.</summary>
    public void RemoveTableRow(BlockEditorViewModel block, TableRowViewModel row)
    {
        if (block.TableRows.Count <= 1)
        {
            return;
        }

        block.RemoveTableRow(row);
        MarkDirty();
    }

    /// <summary>Adds a column to a table block.</summary>
    public void AddTableColumn(BlockEditorViewModel block)
    {
        block.AddTableColumn();
        MarkDirty();
    }

    /// <summary>Removes the last column of a table block.</summary>
    public void RemoveTableColumn(BlockEditorViewModel block)
    {
        if (block.TableRows.Count == 0 || block.TableRows[0].Cells.Count <= 2)
        {
            return;
        }

        block.RemoveTableColumn();
        MarkDirty();
    }

    /// <summary>Adds an empty paragraph after the last block.</summary>
    [RelayCommand]
    private void AddBlock()
    {
        if (!IsLoaded)
        {
            return;
        }

        var block = InsertBlock(Blocks.Count, BlockType.Paragraph);
        MarkDirty();
        block.RequestFocus(0);
    }

    /// <summary>Toggles the saved favorite flag.</summary>
    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            var updated = await _pages.UpdateAsync(
                PageId,
                new PageChanges(null, null, !IsFavorite, null, null));

            if (updated is null)
            {
                _toasts.ShowWarning("The note is no longer available.");
                return;
            }

            IsFavorite = updated.IsFavorite;
            Saved?.Invoke(
                this,
                new PageSavedEventArgs(updated.Id, updated.Title, updated.Icon, updated.IsFavorite, updated.UpdatedAt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling the favorite flag of page {PageId} failed.", PageId);
            _toasts.ShowError("The favorite could not be updated.");
        }
    }

    /// <summary>Adds the typed tag to the page.</summary>
    [RelayCommand]
    private async Task AddTagAsync()
    {
        var value = TagInput?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return;
        }

        if (value.Length > 64)
        {
            _toasts.ShowWarning("Tag names cannot be longer than 64 characters.");
            return;
        }

        if (Tags.Any(tag => string.Equals(tag, value, StringComparison.OrdinalIgnoreCase)))
        {
            TagInput = string.Empty;
            return;
        }

        if (Tags.Count >= MaximumTags)
        {
            _toasts.ShowWarning($"A note cannot have more than {MaximumTags} tags.");
            return;
        }

        Tags.Add(value);
        TagInput = string.Empty;
        await SaveTagsAsync();
    }

    /// <summary>Removes a tag from the page.</summary>
    [RelayCommand]
    private async Task RemoveTagAsync(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || !Tags.Remove(tag))
        {
            return;
        }

        await SaveTagsAsync();
    }

    /// <summary>Opens or closes the version history panel.</summary>
    [RelayCommand]
    private async Task ToggleHistoryAsync()
    {
        IsHistoryOpen = !IsHistoryOpen;
        IsIconPickerOpen = false;

        if (IsHistoryOpen)
        {
            await LoadRevisionsAsync();
        }
    }

    /// <summary>Applies a saved revision to the page.</summary>
    [RelayCommand]
    private async Task RestoreRevisionAsync(RevisionItemViewModel? item)
    {
        if (!IsLoaded || item is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Restore this version?",
                "The current content will be replaced by the selected version. A new revision is created first, so the current state stays in the history.",
                "Restore"))
        {
            return;
        }

        try
        {
            var (title, icon, drafts) = ParseSnapshot(item.Revision);
            if (drafts.Count == 0)
            {
                _toasts.ShowWarning("That version does not contain any blocks.");
                return;
            }

            CancelScheduledSave();
            IsDirty = false;

            var updated = await _pages.UpdateAsync(
                PageId,
                new PageChanges(title, icon ?? string.Empty, null, drafts, null));

            if (updated is null)
            {
                _toasts.ShowWarning("The note is no longer available.");
                return;
            }

            var page = await _pages.GetAsync(PageId);
            if (page is not null)
            {
                _loadVersion++;
                Populate(page);
                await LoadRevisionsAsync();
                await HydrateAttachmentsAsync();
            }

            IsHistoryOpen = false;
            _toasts.ShowSuccess("The earlier version is back in the editor.", "Version restored");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restoring a revision of page {PageId} failed.", PageId);
            _toasts.ShowError("That version could not be restored.");
        }
    }

    /// <summary>Exports the current editor state as Markdown.</summary>
    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            var suggested = MakeSafeFileName(string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title) + ".md";
            var path = _dialogs.PickSaveFile("Export as Markdown", "Markdown|*.md|Text files|*.txt", suggested);
            if (path is null)
            {
                return;
            }

            var markdown = MarkdownCodec.ToMarkdown(Title, BuildDrafts());
            await File.WriteAllTextAsync(path, markdown);
            _toasts.ShowSuccess("The note was exported as Markdown.", "Export complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting page {PageId} as Markdown failed.", PageId);
            _toasts.ShowError("The Markdown file could not be written.");
        }
    }

    /// <summary>Moves the page to the trash after the shell asks for confirmation.</summary>
    [RelayCommand]
    private void RequestDelete() => DeleteRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Selects or clears the page emoji.</summary>
    [RelayCommand]
    private void ChooseIcon(string? emoji)
    {
        if (!IsLoaded || string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        Icon = string.Equals(Icon, emoji, StringComparison.Ordinal) ? string.Empty : emoji;
        IsIconPickerOpen = false;
        MarkDirty();
    }

    /// <summary>Toggles the emoji picker.</summary>
    [RelayCommand]
    private void ToggleIconPicker()
    {
        IsIconPickerOpen = !IsIconPickerOpen;
        if (IsIconPickerOpen)
        {
            IsHistoryOpen = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancelScheduledSave();
        _saveDebounce?.Dispose();
        _saveGate.Dispose();
    }

    partial void OnTitleChanged(string value) => MarkDirty();

    partial void OnIconChanged(string value) => MarkDirty();

    private void Populate(Page page)
    {
        _isLoading = true;

        try
        {
            PageId = page.Id;
            Title = page.Title;
            Icon = page.Icon ?? string.Empty;
            IsFavorite = page.IsFavorite;
            IsLoaded = true;
            TagInput = string.Empty;
            IsHistoryOpen = false;
            IsIconPickerOpen = false;

            Tags.Clear();
            foreach (var name in page.PageTags
                         .Select(pageTag => pageTag.Tag?.Name)
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                         .Select(name => name!))
            {
                Tags.Add(name);
            }

            foreach (var block in Blocks)
            {
                block.PropertyChanged -= OnBlockPropertyChanged;
            }

            Blocks.Clear();
            foreach (var block in page.Blocks.OrderBy(block => block.Position))
            {
                var model = BlockEditorViewModel.FromBlock(this, block);
                AttachBlock(model, Blocks.Count);
            }

            if (Blocks.Count == 0)
            {
                AttachBlock(new BlockEditorViewModel(this, BlockType.Paragraph), 0);
            }

            RefreshListNumbers();
            IsDirty = false;
            SaveStatus = "Saved " + page.UpdatedAt.ToLocalTime().ToString("t");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task LoadRevisionsAsync()
    {
        var version = _loadVersion;

        try
        {
            var revisions = await _pages.GetRevisionsAsync(PageId, 30);
            if (version != _loadVersion)
            {
                return;
            }

            Revisions.Clear();
            foreach (var revision in revisions)
            {
                Revisions.Add(new RevisionItemViewModel(revision));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading revisions of page {PageId} failed.", PageId);
        }
    }

    private async Task HydrateAttachmentsAsync()
    {
        var version = _loadVersion;

        foreach (var block in Blocks.ToArray())
        {
            if (version != _loadVersion)
            {
                return;
            }

            if (block.Type is not (BlockType.Image or BlockType.File) || block.AttachmentId is not { } attachmentId)
            {
                continue;
            }

            try
            {
                var attachment = await _attachments.GetAsync(attachmentId);
                if (version != _loadVersion)
                {
                    return;
                }

                if (attachment is null)
                {
                    block.PreviewError = "The file is no longer available.";
                    continue;
                }

                block.AttachmentSizeText = ByteFormatter.FormatSize(attachment.SizeBytes);

                if (string.IsNullOrWhiteSpace(block.AttachmentName))
                {
                    _suppressDirty = true;
                    try
                    {
                        block.AttachmentName = attachment.FileName;
                    }
                    finally
                    {
                        _suppressDirty = false;
                    }
                }

                if (block.Type == BlockType.Image)
                {
                    await LoadPreviewAsync(block, attachment, version);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Loading attachment {AttachmentId} failed.", attachmentId);
                if (block.Type == BlockType.Image)
                {
                    block.PreviewError = "The image could not be loaded.";
                    block.IsPreviewLoading = false;
                }
            }
        }
    }

    private async Task LoadPreviewAsync(BlockEditorViewModel block, Attachment attachment, int version)
    {
        if (!AttachmentPreviewService.CanPreviewImage(attachment))
        {
            block.PreviewError = "Preview is not available for this image format.";
            return;
        }

        block.IsPreviewLoading = true;
        block.PreviewError = null;

        try
        {
            var image = await _previews.LoadImageAsync(attachment, 1000);
            if (version != _loadVersion)
            {
                return;
            }

            block.PreviewImage = image;
            if (image is null)
            {
                block.PreviewError = "The image could not be decoded.";
            }
        }
        finally
        {
            block.IsPreviewLoading = false;
        }
    }

    private async Task ImportIntoBlockAsync(BlockEditorViewModel block, string path, BlockType type)
    {
        try
        {
            var attachment = await _attachments.ImportAsync(path, PageId);
            if (!IsLoaded || !Blocks.Contains(block))
            {
                return;
            }

            block.Type = type;
            block.AttachmentId = attachment.Id;
            block.AttachmentName = attachment.FileName;
            block.AttachmentSizeText = ByteFormatter.FormatSize(attachment.SizeBytes);
            block.RemoteUrl = null;
            block.PreviewImage = null;
            block.PreviewError = null;

            if (type == BlockType.Image)
            {
                await LoadPreviewAsync(block, attachment, _loadVersion);
            }

            MarkDirty();
            _toasts.ShowSuccess($"{attachment.FileName} was added to the note.", "Attachment added");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Importing {FilePath} into page {PageId} failed.", path, PageId);
            _toasts.ShowError("The file could not be attached. Check the log for details.");
        }
    }

    private async Task SaveTagsAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        await _saveGate.WaitAsync();

        try
        {
            var updated = await _pages.UpdateAsync(
                PageId,
                new PageChanges(null, null, null, null, Tags.ToArray()));

            if (updated is not null)
            {
                Saved?.Invoke(
                    this,
                    new PageSavedEventArgs(updated.Id, updated.Title, updated.Icon, updated.IsFavorite, updated.UpdatedAt));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving tags of page {PageId} failed.", PageId);
            _toasts.ShowError("The tags could not be saved.");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void MarkDirty()
    {
        if (!IsLoaded || _isLoading || _suppressDirty)
        {
            return;
        }

        IsDirty = true;
        SaveStatus = "Unsaved changes";
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        CancelScheduledSave();
        var cancellation = new CancellationTokenSource();
        _saveDebounce = cancellation;
        _ = RunDebouncedSaveAsync(cancellation.Token);
    }

    private async Task RunDebouncedSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutosaveDelayMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await SaveAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed while saving.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The debounced save of page {PageId} failed.", PageId);
        }
    }

    private void CancelScheduledSave()
    {
        var cancellation = _saveDebounce;
        _saveDebounce = null;

        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private BlockEditorViewModel InsertBlock(int index, BlockType type, string text = "")
    {
        var block = new BlockEditorViewModel(this, type)
        {
            Text = text
        };

        index = Math.Clamp(index, 0, Blocks.Count);
        AttachBlock(block, index);
        RefreshListNumbers();
        return block;
    }

    private void AttachBlock(BlockEditorViewModel block, int index)
    {
        block.PropertyChanged += OnBlockPropertyChanged;
        Blocks.Insert(Math.Clamp(index, 0, Blocks.Count), block);
    }

    private void RemoveBlock(BlockEditorViewModel block)
    {
        block.PropertyChanged -= OnBlockPropertyChanged;
        Blocks.Remove(block);
        RefreshListNumbers();
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoading || !IsLoaded)
        {
            return;
        }

        if (e.PropertyName is nameof(BlockEditorViewModel.Text)
            or nameof(BlockEditorViewModel.Type)
            or nameof(BlockEditorViewModel.IsChecked)
            or nameof(BlockEditorViewModel.Language)
            or nameof(BlockEditorViewModel.AttachmentId)
            or nameof(BlockEditorViewModel.AttachmentName))
        {
            MarkDirty();
        }
    }

    private void RefreshListNumbers()
    {
        var number = 0;

        foreach (var block in Blocks)
        {
            if (block.Type == BlockType.NumberedList)
            {
                number++;
                block.ListNumber = number;
            }
            else
            {
                number = 0;
                block.ListNumber = 0;
            }
        }
    }

    private bool TryApplyMarkdownShortcut(BlockEditorViewModel block, string text)
    {
        if (block.Type != BlockType.Paragraph)
        {
            return false;
        }

        BlockType? type = null;
        var isChecked = false;

        if (text == "# ")
        {
            type = BlockType.Heading1;
        }
        else if (text == "## ")
        {
            type = BlockType.Heading2;
        }
        else if (text == "### ")
        {
            type = BlockType.Heading3;
        }
        else if (text is "- " or "* " or "+ ")
        {
            type = BlockType.BulletedList;
        }
        else if (text == "> ")
        {
            type = BlockType.Quote;
        }
        else if (text is "[ ] " or "[] ")
        {
            type = BlockType.ToDo;
        }
        else if (text == "[x] ")
        {
            type = BlockType.ToDo;
            isChecked = true;
        }
        else if (text == "```")
        {
            type = BlockType.Code;
        }
        else if (IsNumberedPrefix(text))
        {
            type = BlockType.NumberedList;
        }

        if (type is null)
        {
            return false;
        }

        block.Text = string.Empty;
        block.Type = type.Value;
        if (type == BlockType.ToDo)
        {
            block.IsChecked = isChecked;
        }

        block.IsSlashMenuOpen = false;
        MarkDirty();
        block.RequestFocus(0);
        return true;
    }

    private void UpdateSlashMenu(BlockEditorViewModel block, string text)
    {
        if (text.Length is > 0 and <= 32 &&
            text[0] == '/' &&
            !text.Contains(' ') &&
            !text.Contains('\n'))
        {
            var query = text[1..];
            var matches = SlashCatalog.Where(command => command.Matches(query)).ToArray();
            if (matches.Length > 0)
            {
                block.SetSlashItems(matches);
                block.IsSlashMenuOpen = true;
                return;
            }
        }

        block.IsSlashMenuOpen = false;
    }

    private static bool IsNumberedPrefix(string text)
    {
        if (text.Length is < 3 or > 5 || text[^1] != ' ' || text[^2] != '.')
        {
            return false;
        }

        for (var i = 0; i < text.Length - 2; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private BlockDraft[] BuildDrafts()
    {
        if (Blocks.Count == 0)
        {
            return [new BlockDraft(BlockType.Paragraph, "{\"text\":\"\"}")];
        }

        return Blocks.Select(block => block.ToDraft()).ToArray();
    }

    private (string Title, string? Icon, List<BlockDraft> Blocks) ParseSnapshot(Revision revision)
    {
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(revision.ContentSnapshot) ? "{}" : revision.ContentSnapshot);
        var root = document.RootElement;

        var title = root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString() ?? "Untitled"
            : "Untitled";

        var icon = root.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind == JsonValueKind.String
            ? iconElement.GetString()
            : null;

        var drafts = new List<BlockDraft>();

        if (root.TryGetProperty("blocks", out var blocksElement) && blocksElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blocksElement.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !block.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<BlockType>(typeElement.GetString(), ignoreCase: true, out var type) ||
                    !block.TryGetProperty("content", out var contentElement) ||
                    contentElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var content = contentElement.GetString();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    drafts.Add(new BlockDraft(type, content));
                }
            }
        }

        return (string.IsNullOrWhiteSpace(title) ? "Untitled" : title, icon, drafts);
    }

    private static string NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "Untitled" : result;
    }
}

/// <summary>Details of a successful page save.</summary>
/// <param name="PageId">Page that was saved.</param>
/// <param name="Title">Normalized title.</param>
/// <param name="Icon">Page icon, when set.</param>
/// <param name="IsFavorite">Favorite flag.</param>
/// <param name="UpdatedAt">Server timestamp of the saved page.</param>
public sealed record PageSavedEventArgs(
    Guid PageId,
    string Title,
    string? Icon,
    bool IsFavorite,
    DateTimeOffset UpdatedAt);

/// <summary>A saved version shown in the history panel.</summary>
public sealed class RevisionItemViewModel
{
    /// <summary>Creates the item from a stored revision.</summary>
    public RevisionItemViewModel(Revision revision)
    {
        Revision = revision ?? throw new ArgumentNullException(nameof(revision));
        TimeText = revision.CreatedAt.ToLocalTime().ToString("MMM d, yyyy · h:mm tt");
    }

    /// <summary>The revision entity.</summary>
    public Revision Revision { get; }

    /// <summary>Local time when the version was captured.</summary>
    public string TimeText { get; }
}
