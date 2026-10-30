using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotedX.Core.Domain;
using NotedX.Core.Notes;
using NotedX.Desktop.Services;
using NotedX.Desktop.Settings;
using NotedX.Server;
using NotedX.Server.Realtime;

namespace NotedX.Desktop.ViewModels;

/// <summary>Coordinates the navigation tree, editor, search, files, pairing, and settings.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private enum ShellSection
    {
        Home,
        Editor,
        Trash,
        Files
    }

    private readonly NotedXWebServer _server;
    private readonly PageService _pages;
    private readonly ToastService _toasts;
    private readonly DesktopDialogService _dialogs;
    private readonly ThemeService _theme;
    private readonly DesktopPreferences _preferences;
    private readonly PreferencesStore _preferencesStore;
    private readonly ILogger<MainViewModel> _logger;

    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly SemaphoreSlim _treeGate = new(1, 1);
    private readonly List<PageTreeNodeViewModel> _nodes = new();
    private readonly Dictionary<Guid, PageTreeNodeViewModel> _nodesById = new();

    private bool _suppressNodeOpen;
    private bool _initialized;
    private bool _disposed;

    [ObservableProperty]
    private PageTreeNodeViewModel? _selectedPage;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private bool _isHomeVisible = true;

    [ObservableProperty]
    private bool _isEditorVisible;

    [ObservableProperty]
    private bool _isTrashVisible;

    [ObservableProperty]
    private bool _isFilesVisible;

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private bool _isPairingOpen;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isTransfersOpen;

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private bool _hasAnyPages;

    [ObservableProperty]
    private int _trashCount;

    [ObservableProperty]
    private string _serverSummary = string.Empty;

    /// <summary>Creates the shell over the running local server and the child view models.</summary>
    public MainViewModel(
        NotedXWebServer server,
        PageEditorViewModel editor,
        SearchViewModel search,
        FilesViewModel files,
        TransfersViewModel transfers,
        PairingViewModel pairing,
        SettingsViewModel settings,
        ThemeService theme,
        ToastService toasts,
        DesktopDialogService dialogs,
        DesktopPreferences preferences,
        PreferencesStore preferencesStore,
        ILogger<MainViewModel> logger)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _pages = _server.Pages;
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Search = search ?? throw new ArgumentNullException(nameof(search));
        Files = files ?? throw new ArgumentNullException(nameof(files));
        Transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        Pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Toasts = toasts;

        _isSidebarCollapsed = _preferences.SidebarCollapsed;
        _isDarkTheme = _theme.IsDark;
        Editor.PropertyChanged += OnEditorPropertyChanged;
    }

    /// <summary>Root pages shown in the navigation tree.</summary>
    public ObservableCollection<PageTreeNodeViewModel> Pages { get; } = new();

    /// <summary>Favorite pages, sorted by title.</summary>
    public ObservableCollection<PageTreeNodeViewModel> Favorites { get; } = new();

    /// <summary>Recently edited pages, newest first.</summary>
    public ObservableCollection<PageTreeNodeViewModel> RecentPages { get; } = new();

    /// <summary>Deleted pages that can be restored.</summary>
    public ObservableCollection<TrashItemViewModel> TrashItems { get; } = new();

    /// <summary>The page editor.</summary>
    public PageEditorViewModel Editor { get; }

    /// <summary>Full-text search overlay.</summary>
    public SearchViewModel Search { get; }

    /// <summary>File browser.</summary>
    public FilesViewModel Files { get; }

    /// <summary>Live transfers from paired phones.</summary>
    public TransfersViewModel Transfers { get; }

    /// <summary>Pairing panel.</summary>
    public PairingViewModel Pairing { get; }

    /// <summary>Preferences and diagnostics panel.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Toast notifications shown in the shell.</summary>
    public ToastService Toasts { get; }

    /// <summary>Product name shown on the welcome screen.</summary>
    public string ProductName => "noted-x";

    /// <summary>One-line value proposition.</summary>
    public string Tagline => "Your notes, on your network.";

    /// <summary>Short sentence describing the privacy model.</summary>
    public string PrivacyNote => "Everything stays on your PC and the devices you pair.";

    /// <summary>Display version of the desktop build.</summary>
    public string VersionText =>
        $"Version {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";

    /// <summary>Whether any favorite exists.</summary>
    public bool HasFavorites => Favorites.Count > 0;

    /// <summary>Whether any page was edited recently.</summary>
    public bool HasRecentPages => RecentPages.Count > 0;

    /// <summary>Whether the trash contains pages.</summary>
    public bool HasTrash => TrashItems.Count > 0;

    /// <summary>Whether the embedded server is listening.</summary>
    public bool IsServerRunning => _server.IsRunning;

    /// <summary>Title shown in the window title bar.</summary>
    public string PageHeader
    {
        get
        {
            if (IsHomeVisible)
            {
                return "Home";
            }

            if (IsTrashVisible)
            {
                return "Trash";
            }

            if (IsFilesVisible)
            {
                return "Files";
            }

            if (!Editor.IsLoaded)
            {
                return "noted-x";
            }

            return string.IsNullOrWhiteSpace(Editor.Title) ? "Untitled" : Editor.Title.Trim();
        }
    }

    /// <summary>Loads the workspace and opens the most recently edited note.</summary>
    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        ServerSummary = BuildServerSummary();

        _theme.Changed += OnThemeChanged;
        Editor.Saved += OnPageSaved;
        Editor.DeleteRequested += OnEditorDeleteRequested;
        Search.ResultChosen += OnSearchResultChosen;
        Files.PageRequested += OnFilePageRequested;
        _server.Realtime.PageChanged += OnPageChanged;

        await RefreshTreeAsync();
        await RefreshTrashAsync();

        if (RecentPages.Count > 0)
        {
            await OpenPageAsync(RecentPages[0].Id);
        }
        else
        {
            ShowSection(ShellSection.Home);
        }
    }

    /// <summary>Saves pending edits before the process exits.</summary>
    public async Task<bool> PrepareShutdownAsync()
    {
        try
        {
            return await Editor.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flushing the editor before shutdown failed.");
            return false;
        }
    }

    /// <summary>Starts a quiet save when the window is hidden to the tray.</summary>
    public void FlushEditor() => _ = Editor.FlushAsync();

    /// <summary>Creates a root-level page and opens it.</summary>
    [RelayCommand]
    private Task NewPageAsync() => CreatePageAsync(null);

    /// <summary>Creates a page inside the supplied page.</summary>
    [RelayCommand]
    private Task NewSubpageAsync(PageTreeNodeViewModel? parent) => CreatePageAsync(parent?.Id);

    /// <summary>Opens a page by identifier.</summary>
    [RelayCommand]
    private async Task OpenPageAsync(Guid pageId)
    {
        if (_disposed)
        {
            return;
        }

        await _openGate.WaitAsync();

        try
        {
            var page = await _pages.GetAsync(pageId);
            if (page is null)
            {
                _toasts.ShowWarning("That note was moved to the trash or deleted.");
                await RefreshTreeAsync();
                return;
            }

            await Editor.OpenAsync(page);
            if (!Editor.IsLoaded || Editor.PageId != page.Id)
            {
                if (Editor.IsLoaded && _nodesById.TryGetValue(Editor.PageId, out var previous))
                {
                    SelectNode(previous);
                }

                return;
            }

            if (_nodesById.TryGetValue(page.Id, out var node))
            {
                SelectNode(node);
            }

            ShowSection(ShellSection.Editor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening page {PageId} failed.", pageId);
            _toasts.ShowError("The note could not be opened.");
        }
        finally
        {
            _openGate.Release();
        }
    }

    /// <summary>Opens a page chosen from the tree, favorites, or recent lists.</summary>
    [RelayCommand]
    private Task OpenNodeAsync(PageTreeNodeViewModel? node) =>
        node is null ? Task.CompletedTask : OpenPageAsync(node.Id);

    /// <summary>Shows the welcome screen with recent notes.</summary>
    [RelayCommand]
    private void ShowHome()
    {
        ClearSelection();
        ShowSection(ShellSection.Home);
    }

    /// <summary>Shows the trash.</summary>
    [RelayCommand]
    private async Task ShowTrashAsync()
    {
        await RefreshTrashAsync();
        ClearSelection();
        ShowSection(ShellSection.Trash);
    }

    /// <summary>Shows the file browser.</summary>
    [RelayCommand]
    private async Task ShowFilesAsync()
    {
        await Files.RefreshAsync();
        ClearSelection();
        ShowSection(ShellSection.Files);
    }

    /// <summary>Collapses or expands the navigation sidebar.</summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    /// <summary>Switches between light and dark appearance.</summary>
    [RelayCommand]
    private void ToggleTheme() => _theme.Toggle();

    /// <summary>Opens the search overlay.</summary>
    [RelayCommand]
    private void OpenSearch()
    {
        Search.Activate();
        IsSearchOpen = true;
    }

    /// <summary>Closes the search overlay.</summary>
    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    /// <summary>Opens the pairing panel with a fresh code.</summary>
    [RelayCommand]
    private async Task OpenPairingAsync()
    {
        IsPairingOpen = true;

        try
        {
            await Pairing.PrepareAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The pairing panel could not be prepared.");
        }
    }

    /// <summary>Opens the settings panel.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        Settings.Refresh();
        IsSettingsOpen = true;
    }

    /// <summary>Shows or hides the transfers panel.</summary>
    [RelayCommand]
    private void ToggleTransfers() => IsTransfersOpen = !IsTransfersOpen;

    /// <summary>Saves the open note immediately.</summary>
    [RelayCommand]
    private async Task SaveNowAsync()
    {
        if (!Editor.IsLoaded)
        {
            return;
        }

        var wasDirty = Editor.IsDirty;

        try
        {
            if (await Editor.FlushAsync())
            {
                _toasts.ShowSuccess(
                    wasDirty ? "Your changes are on disk." : "Everything is already saved.",
                    "Saved");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the open note on demand failed.");
            _toasts.ShowError("The note could not be saved.");
        }
    }

    /// <summary>Closes the pairing panel.</summary>
    [RelayCommand]
    private void ClosePairing() => IsPairingOpen = false;

    /// <summary>Closes the settings panel.</summary>
    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    /// <summary>Dismisses a toast notification.</summary>
    [RelayCommand]
    private void DismissToast(ToastItem? toast)
    {
        if (toast is not null)
        {
            Toasts.Dismiss(toast.Id);
        }
    }

    /// <summary>Moves the open note and its subpages to the trash.</summary>
    [RelayCommand]
    private async Task DeleteCurrentPageAsync()
    {
        if (!Editor.IsLoaded || Editor.PageId == Guid.Empty)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(Editor.Title) ? "this note" : $"“{Editor.Title}”";
        if (!_dialogs.Confirm(
                "Move to trash?",
                $"{title} and its subpages will move to the trash. You can restore them later.",
                "Move to trash"))
        {
            return;
        }

        var pageId = Editor.PageId;

        try
        {
            Editor.Close();
            ClearSelection();

            var deleted = await _pages.SoftDeleteAsync(pageId);
            if (deleted)
            {
                _toasts.ShowSuccess("You can restore it from the trash.", "Moved to trash");
            }
            else
            {
                _toasts.ShowWarning("The note was already removed.");
            }

            await RefreshTreeAsync();
            await RefreshTrashAsync();

            if (RecentPages.Count > 0)
            {
                await OpenPageAsync(RecentPages[0].Id);
            }
            else
            {
                ShowSection(ShellSection.Home);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moving page {PageId} to the trash failed.", pageId);
            _toasts.ShowError("The note could not be moved to the trash.");
        }
    }

    /// <summary>Restores a trashed page.</summary>
    [RelayCommand]
    private async Task RestorePageAsync(TrashItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var restored = await _pages.RestoreAsync(item.PageId);
            await RefreshTrashAsync();
            await RefreshTreeAsync();

            if (restored)
            {
                _toasts.ShowSuccess($"“{item.Title}” is back in your notes.", "Note restored");
            }
            else
            {
                _toasts.ShowWarning("The note could not be restored.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restoring page {PageId} failed.", item.PageId);
            _toasts.ShowError("The note could not be restored.");
        }
    }

    /// <summary>Deletes one trashed page permanently.</summary>
    [RelayCommand]
    private async Task DeleteForeverAsync(TrashItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Delete forever?",
                $"“{item.Title}” and its subpages will be removed permanently. This cannot be undone.",
                "Delete forever",
                isDestructive: true))
        {
            return;
        }

        try
        {
            await _pages.DeletePermanentlyAsync(item.PageId);
            await RefreshTrashAsync();
            _toasts.ShowSuccess("The note was deleted permanently.", "Deleted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting page {PageId} permanently failed.", item.PageId);
            _toasts.ShowError("The note could not be deleted.");
        }
    }

    /// <summary>Empties the trash.</summary>
    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        if (TrashItems.Count == 0)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Empty the trash?",
                "Every note in the trash will be removed permanently. This cannot be undone.",
                "Empty trash",
                isDestructive: true))
        {
            return;
        }

        try
        {
            var roots = TrashItems
                .Where(item => item.ParentId is not { } parent ||
                               !TrashItems.Any(candidate => candidate.PageId == parent))
                .ToArray();

            foreach (var item in roots)
            {
                await _pages.DeletePermanentlyAsync(item.PageId);
            }

            await RefreshTrashAsync();
            _toasts.ShowSuccess("The trash is empty.", "Trash emptied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emptying the trash failed.");
            _toasts.ShowError("Some notes could not be deleted.");
        }
    }

    /// <summary>Imports a Markdown file as a new note.</summary>
    [RelayCommand]
    private async Task ImportMarkdownAsync()
    {
        var path = _dialogs.PickFile(
            "Import Markdown",
            "Markdown|*.md;*.markdown;*.txt|All files|*.*");

        if (path is null)
        {
            return;
        }

        try
        {
            var markdown = await File.ReadAllTextAsync(path);
            var document = MarkdownCodec.Parse(markdown, Path.GetFileNameWithoutExtension(path));
            var blocks = document.Blocks.Count > 0
                ? document.Blocks
                : new[] { new BlockDraft(BlockType.Paragraph, "{\"text\":\"\"}") };
            var page = await _pages.CreateAsync(
                new PageDraft(null, null, document.Title, null, blocks));

            await RefreshTreeAsync(page.Id);
            await OpenPageAsync(page.Id);
            _toasts.ShowSuccess($"“{page.Title}” is ready to edit.", "Markdown imported");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Importing Markdown from {FilePath} failed.", path);
            _toasts.ShowError("The Markdown file could not be imported.");
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
        Editor.PropertyChanged -= OnEditorPropertyChanged;
        Editor.Saved -= OnPageSaved;
        Editor.DeleteRequested -= OnEditorDeleteRequested;
        Search.ResultChosen -= OnSearchResultChosen;
        Files.PageRequested -= OnFilePageRequested;
        _server.Realtime.PageChanged -= OnPageChanged;
        DetachNodes();
        _openGate.Dispose();
        _treeGate.Dispose();
    }

    partial void OnSelectedPageChanged(PageTreeNodeViewModel? value)
    {
        if (_suppressNodeOpen || value is null || !_initialized || _disposed)
        {
            return;
        }

        _ = OpenPageAsync(value.Id);
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        _preferences.SidebarCollapsed = value;

        if (!_preferencesStore.Save(_preferences))
        {
            _logger.LogDebug("The sidebar preference could not be saved.");
        }
    }

    private async Task CreatePageAsync(Guid? parentId)
    {
        try
        {
            var page = await _pages.CreateAsync(
                new PageDraft(
                    null,
                    parentId,
                    "Untitled",
                    null,
                    new[] { new BlockDraft(BlockType.Paragraph, "{\"text\":\"\"}") }));

            await RefreshTreeAsync(page.Id);
            await OpenPageAsync(page.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating a page under {ParentId} failed.", parentId);
            _toasts.ShowError("A new note could not be created.");
        }
    }

    private async Task RefreshTreeAsync(Guid? preferredSelection = null)
    {
        await _treeGate.WaitAsync();

        try
        {
            if (_disposed)
            {
                return;
            }

            var expanded = _nodes.Where(node => node.IsExpanded).Select(node => node.Id).ToHashSet();
            var selectionId = preferredSelection ?? SelectedPage?.Id;
            IReadOnlyList<Page> pages;

            try
            {
                pages = await _pages.ListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loading the page tree failed.");
                _toasts.ShowError("The note list could not be loaded.");
                return;
            }

            if (_disposed)
            {
                return;
            }

            DetachNodes();
            Pages.Clear();

            var ordered = pages
                .OrderBy(page => page.Position)
                .ThenBy(page => page.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            foreach (var page in ordered)
            {
                _nodesById[page.Id] = new PageTreeNodeViewModel(page);
            }

            var roots = new List<PageTreeNodeViewModel>(ordered.Length);

            foreach (var page in ordered)
            {
                var node = _nodesById[page.Id];

                if (page.ParentId is { } parentId &&
                    _nodesById.TryGetValue(parentId, out var parent) &&
                    !ReferenceEquals(parent, node))
                {
                    node.Parent = parent;
                    parent.Children.Add(node);
                }
                else
                {
                    roots.Add(node);
                }
            }

            foreach (var root in roots)
            {
                Pages.Add(root);
            }

            AttachNodes(_nodesById.Values);

            foreach (var node in _nodes)
            {
                node.IsExpanded = expanded.Contains(node.Id);
            }

            RebuildShortcuts();
            HasAnyPages = Pages.Count > 0;

            if (selectionId is { } id && _nodesById.TryGetValue(id, out var selected))
            {
                SelectNode(selected);
            }
            else
            {
                _suppressNodeOpen = true;
                try
                {
                    SelectedPage = null;
                }
                finally
                {
                    _suppressNodeOpen = false;
                }
            }

            NotifyHeader();
        }
        finally
        {
            _treeGate.Release();
        }
    }

    private async Task RefreshTrashAsync()
    {
        try
        {
            var pages = await _pages.ListAsync(includeDeleted: true);
            var deleted = pages
                .Where(page => page.IsDeleted)
                .OrderByDescending(page => page.DeletedAt ?? DateTimeOffset.MinValue)
                .ThenBy(page => page.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            TrashItems.Clear();
            foreach (var page in deleted)
            {
                TrashItems.Add(new TrashItemViewModel(page));
            }

            TrashCount = TrashItems.Count;
            OnPropertyChanged(nameof(HasTrash));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading the trash failed.");
        }
    }

    private void RebuildShortcuts()
    {
        Favorites.Clear();
        foreach (var node in _nodes
                     .Where(node => node.IsFavorite)
                     .OrderBy(node => node.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            Favorites.Add(node);
        }

        RecentPages.Clear();
        foreach (var node in _nodes.OrderByDescending(node => node.UpdatedAt).Take(6))
        {
            RecentPages.Add(node);
        }

        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasRecentPages));
    }

    private void AttachNodes(IEnumerable<PageTreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            _nodes.Add(node);
            node.PropertyChanged += OnNodePropertyChanged;
        }
    }

    private void DetachNodes()
    {
        foreach (var node in _nodes)
        {
            node.PropertyChanged -= OnNodePropertyChanged;
        }

        _nodes.Clear();
        _nodesById.Clear();
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressNodeOpen ||
            _disposed ||
            e.PropertyName != nameof(PageTreeNodeViewModel.IsSelected) ||
            sender is not PageTreeNodeViewModel { IsSelected: true } node)
        {
            return;
        }

        SelectedPage = node;
    }

    private void SelectNode(PageTreeNodeViewModel node)
    {
        _suppressNodeOpen = true;

        try
        {
            foreach (var candidate in _nodes)
            {
                var shouldSelect = ReferenceEquals(candidate, node);
                if (candidate.IsSelected != shouldSelect)
                {
                    candidate.IsSelected = shouldSelect;
                }
            }

            ExpandAncestors(node);
            SelectedPage = node;
        }
        finally
        {
            _suppressNodeOpen = false;
        }

        NotifyHeader();
    }

    private static void ExpandAncestors(PageTreeNodeViewModel node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
        }
    }

    private void ClearSelection()
    {
        _suppressNodeOpen = true;

        try
        {
            foreach (var node in _nodes)
            {
                node.IsSelected = false;
            }

            SelectedPage = null;
        }
        finally
        {
            _suppressNodeOpen = false;
        }
    }

    private void ShowSection(ShellSection section)
    {
        IsHomeVisible = section == ShellSection.Home;
        IsEditorVisible = section == ShellSection.Editor;
        IsTrashVisible = section == ShellSection.Trash;
        IsFilesVisible = section == ShellSection.Files;
        NotifyHeader();
    }

    private void NotifyHeader() => OnPropertyChanged(nameof(PageHeader));

    private void OnThemeChanged(object? sender, EventArgs e) => IsDarkTheme = _theme.IsDark;

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageEditorViewModel.Title) or nameof(PageEditorViewModel.IsLoaded))
        {
            NotifyHeader();
        }
    }

    private void OnPageSaved(object? sender, PageSavedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_nodesById.TryGetValue(e.PageId, out var node))
        {
            var favoriteChanged = node.IsFavorite != e.IsFavorite;
            node.Apply(e.Title, e.Icon, e.IsFavorite, e.UpdatedAt);

            if (favoriteChanged || IsHomeVisible)
            {
                RebuildShortcuts();
            }
        }

        if (e.PageId == Editor.PageId)
        {
            NotifyHeader();
        }
    }

    private async void OnEditorDeleteRequested(object? sender, EventArgs e)
    {
        try
        {
            await DeleteCurrentPageAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The delete request from the editor failed.");
            _toasts.ShowError("The note could not be moved to the trash.");
        }
    }

    private void OnSearchResultChosen(Guid pageId)
    {
        IsSearchOpen = false;
        _ = OpenPageAsync(pageId);
    }

    private void OnFilePageRequested(Guid pageId)
    {
        _ = OpenPageAsync(pageId);
    }

    private void OnPageChanged(object? sender, PageChangedEvent change) =>
        Dispatch(() => _ = ApplyRemoteChangeAsync(change));

    private async Task ApplyRemoteChangeAsync(PageChangedEvent change)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            switch (change.Change)
            {
                case "updated":
                    if (_nodesById.TryGetValue(change.PageId, out var node))
                    {
                        var page = await _pages.GetAsync(change.PageId);
                        if (page is not null)
                        {
                            node.Apply(page);
                            RebuildShortcuts();
                        }
                    }

                    if (change.PageId == Editor.PageId)
                    {
                        await Editor.ReloadFromServerAsync();
                    }

                    break;

                case "trashed":
                case "deleted":
                    await RefreshTreeAsync();
                    await RefreshTrashAsync();

                    if (change.PageId == Editor.PageId)
                    {
                        Editor.Close();
                        ClearSelection();
                        _toasts.ShowWarning("Another device moved this note to the trash.");

                        if (RecentPages.Count > 0)
                        {
                            await OpenPageAsync(RecentPages[0].Id);
                        }
                        else
                        {
                            ShowSection(ShellSection.Home);
                        }
                    }

                    break;

                default:
                    await RefreshTreeAsync();
                    await RefreshTrashAsync();
                    break;
            }

            NotifyHeader();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A remote page change could not be applied.");
        }
    }

    private string BuildServerSummary()
    {
        if (!_server.IsRunning || _server.BaseUrl is not { } baseUrl)
        {
            return "Local server offline";
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? $"LAN · {uri.Authority}"
            : $"LAN · {baseUrl}";
    }

    private void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
