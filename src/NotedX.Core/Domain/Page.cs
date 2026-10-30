namespace NotedX.Core.Domain;

/// <summary>A note inside a workspace. Pages form a tree through <see cref="ParentId"/>.</summary>
public sealed class Page
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public Guid? ParentId { get; set; }

    public Page? Parent { get; set; }

    public ICollection<Page> Children { get; set; } = new List<Page>();

    public string Title { get; set; } = string.Empty;

    public string? Icon { get; set; }

    public double Position { get; set; }

    public bool IsFavorite { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Denormalized plain text (title plus block text) that feeds the SQLite FTS5 index.
    /// Page services must refresh it whenever content changes.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    public ICollection<Block> Blocks { get; set; } = new List<Block>();

    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();

    public ICollection<Revision> Revisions { get; set; } = new List<Revision>();

    public ICollection<PageTag> PageTags { get; set; } = new List<PageTag>();
}
