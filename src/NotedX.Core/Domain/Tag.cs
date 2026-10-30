namespace NotedX.Core.Domain;

/// <summary>A workspace-scoped label that can be applied to many pages.</summary>
public sealed class Tag
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Optional hexadecimal accent color, for example <c>#6366F1</c>.</summary>
    public string? Color { get; set; }

    public ICollection<PageTag> PageTags { get; set; } = new List<PageTag>();
}
