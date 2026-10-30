using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotedX.Core.Notes;

namespace NotedX.Desktop.ViewModels;

/// <summary>Full-text search over notes, shown in the Ctrl+K overlay.</summary>
public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    private readonly PageService _pages;
    private readonly ILogger<SearchViewModel> _logger;

    private CancellationTokenSource? _debounce;
    private int _searchVersion;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasSearched;

    /// <summary>Creates the search view model.</summary>
    public SearchViewModel(PageService pages, ILogger<SearchViewModel> logger)
    {
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised when a result is activated.</summary>
    public event Action<Guid>? ResultChosen;

    /// <summary>Matching notes.</summary>
    public ObservableCollection<SearchResultViewModel> Results { get; } = new();

    /// <summary>Whether the query is blank.</summary>
    public bool IsEmptyQuery => string.IsNullOrWhiteSpace(Query);

    /// <summary>Whether there are no results for a non-empty query.</summary>
    public bool HasNoResults => HasSearched && !IsSearching && Results.Count == 0 && !IsEmptyQuery;

    /// <summary>Resets the overlay for a fresh search.</summary>
    public void Activate()
    {
        CancelDebounce();
        Query = string.Empty;
        Results.Clear();
        HasSearched = false;
        IsSearching = false;
        OnPropertyChanged(nameof(IsEmptyQuery));
        OnPropertyChanged(nameof(HasNoResults));
    }

    /// <summary>Opens the selected result.</summary>
    [RelayCommand]
    private void Choose(SearchResultViewModel? result)
    {
        if (result is not null)
        {
            ResultChosen?.Invoke(result.PageId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancelDebounce();
    }

    partial void OnQueryChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsEmptyQuery));
        OnPropertyChanged(nameof(HasNoResults));
        CancelDebounce();

        if (string.IsNullOrWhiteSpace(Query))
        {
            _searchVersion++;
            Results.Clear();
            HasSearched = false;
            IsSearching = false;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _debounce = cancellation;
        _ = SearchAfterDelayAsync(cancellation.Token);
    }

    private async Task SearchAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(220, cancellationToken);
            var version = ++_searchVersion;
            IsSearching = true;

            var hits = await _pages.SearchAsync(Query, 20, cancellationToken);
            if (cancellationToken.IsCancellationRequested || version != _searchVersion)
            {
                return;
            }

            Results.Clear();
            foreach (var hit in hits)
            {
                Results.Add(new SearchResultViewModel(hit));
            }

            IsSearching = false;
            HasSearched = true;
            OnPropertyChanged(nameof(HasNoResults));
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke replaced this search.
        }
        catch (Exception ex)
        {
            IsSearching = false;
            HasSearched = true;
            _logger.LogWarning(ex, "Searching notes failed.");
        }
    }

    private void CancelDebounce()
    {
        var cancellation = _debounce;
        _debounce = null;

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
}

/// <summary>A single search hit formatted for the overlay.</summary>
public sealed class SearchResultViewModel
{
    /// <summary>Creates the item from a search hit.</summary>
    public SearchResultViewModel(PageSearchHit hit)
    {
        PageId = hit.Id;
        Title = string.IsNullOrWhiteSpace(hit.Title) ? "Untitled" : hit.Title;
        Icon = string.IsNullOrWhiteSpace(hit.Icon) ? "📄" : hit.Icon;
        Snippet = CleanSnippet(hit.Snippet);
        UpdatedText = hit.UpdatedAt.ToLocalTime().ToString("MMM d, yyyy");
    }

    /// <summary>Identifier of the matching page.</summary>
    public Guid PageId { get; }

    /// <summary>Page title.</summary>
    public string Title { get; }

    /// <summary>Page emoji or a generic document icon.</summary>
    public string Icon { get; }

    /// <summary>Matching excerpt.</summary>
    public string Snippet { get; }

    /// <summary>When the page was last changed.</summary>
    public string UpdatedText { get; }

    private static string CleanSnippet(string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return string.Empty;
        }

        var cleaned = snippet
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return cleaned.Length <= 180 ? cleaned : cleaned[..180].TrimEnd() + "…";
    }
}
