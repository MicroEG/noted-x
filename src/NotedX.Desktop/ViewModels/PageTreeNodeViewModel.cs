using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NotedX.Core.Domain;

namespace NotedX.Desktop.ViewModels;

/// <summary>A page shown in the navigation tree, favorites list, or recent list.</summary>
public sealed partial class PageTreeNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _icon;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private DateTimeOffset _updatedAt;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Creates a tree node from a stored page.</summary>
    public PageTreeNodeViewModel(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        Id = page.Id;
        ParentId = page.ParentId;
        _title = NormalizeTitle(page.Title);
        _icon = NormalizeIcon(page.Icon);
        _isFavorite = page.IsFavorite;
        _updatedAt = page.UpdatedAt;
    }

    /// <summary>Page identifier.</summary>
    public Guid Id { get; }

    /// <summary>Parent page, when the page is nested.</summary>
    public Guid? ParentId { get; }

    /// <summary>Parent node, or <c>null</c> for root pages.</summary>
    public PageTreeNodeViewModel? Parent { get; internal set; }

    /// <summary>Nested pages.</summary>
    public ObservableCollection<PageTreeNodeViewModel> Children { get; } = new();

    /// <summary>Whether the node has nested pages.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>Short local timestamp of the last edit.</summary>
    public string UpdatedText => FormatRelative(UpdatedAt);

    /// <summary>Updates the visible metadata after a save or remote change.</summary>
    public void Apply(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Apply(page.Title, page.Icon, page.IsFavorite, page.UpdatedAt);
    }

    /// <summary>Updates the visible metadata after a save or remote change.</summary>
    public void Apply(string title, string? icon, bool isFavorite, DateTimeOffset updatedAt)
    {
        Title = NormalizeTitle(title);
        Icon = NormalizeIcon(icon);
        IsFavorite = isFavorite;
        UpdatedAt = updatedAt;
    }

    partial void OnUpdatedAtChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(UpdatedText));
    }

    private static string NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();

    private static string NormalizeIcon(string? icon) =>
        string.IsNullOrWhiteSpace(icon) ? "📄" : icon;

    private static string FormatRelative(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        var elapsed = DateTimeOffset.Now - local;

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "Just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)}h ago";
        }

        if (elapsed < TimeSpan.FromDays(7))
        {
            return $"{Math.Max(1, (int)elapsed.TotalDays)}d ago";
        }

        return local.ToString("MMM d");
    }
}
