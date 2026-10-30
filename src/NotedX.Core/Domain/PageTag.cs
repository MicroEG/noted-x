namespace NotedX.Core.Domain;

/// <summary>Join entity linking a page to a tag.</summary>
public sealed class PageTag
{
    public Guid PageId { get; set; }

    public Page? Page { get; set; }

    public Guid TagId { get; set; }

    public Tag? Tag { get; set; }
}
