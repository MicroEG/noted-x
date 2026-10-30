namespace NotedX.Core.Domain;

/// <summary>An immutable snapshot of a page and its blocks, used for version history.</summary>
public sealed class Revision
{
    public Guid Id { get; set; }

    public Guid PageId { get; set; }

    public Page? Page { get; set; }

    /// <summary>Serialized JSON snapshot of the page and its ordered blocks.</summary>
    public string ContentSnapshot { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
