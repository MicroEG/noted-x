using NotedX.Core.Domain;

namespace NotedX.Desktop.ViewModels;

/// <summary>A trashed page shown in the trash view.</summary>
public sealed class TrashItemViewModel
{
    /// <summary>Creates the item from a deleted page.</summary>
    public TrashItemViewModel(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        PageId = page.Id;
        ParentId = page.ParentId;
        Title = string.IsNullOrWhiteSpace(page.Title) ? "Untitled" : page.Title.Trim();
        Icon = string.IsNullOrWhiteSpace(page.Icon) ? "📄" : page.Icon;
        DeletedAt = page.DeletedAt ?? page.UpdatedAt;
        DeletedText = $"Deleted {DeletedAt.ToLocalTime():MMM d, yyyy · h:mm tt}";
    }

    /// <summary>Page identifier.</summary>
    public Guid PageId { get; }

    /// <summary>Parent page at the time of deletion.</summary>
    public Guid? ParentId { get; }

    /// <summary>Display title.</summary>
    public string Title { get; }

    /// <summary>Page emoji or a generic document icon.</summary>
    public string Icon { get; }

    /// <summary>When the page was moved to the trash.</summary>
    public DateTimeOffset DeletedAt { get; }

    /// <summary>Local deletion timestamp.</summary>
    public string DeletedText { get; }
}
