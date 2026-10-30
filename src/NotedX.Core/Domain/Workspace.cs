namespace NotedX.Core.Domain;

/// <summary>A top-level container for related pages and tags.</summary>
public sealed class Workspace
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Page> Pages { get; set; } = new List<Page>();

    public ICollection<Tag> Tags { get; set; } = new List<Tag>();
}
