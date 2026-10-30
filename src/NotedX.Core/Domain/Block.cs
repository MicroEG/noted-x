namespace NotedX.Core.Domain;

/// <summary>A single content block inside a page.</summary>
public sealed class Block
{
    public Guid Id { get; set; }

    public Guid PageId { get; set; }

    public Page? Page { get; set; }

    public BlockType Type { get; set; } = BlockType.Paragraph;

    /// <summary>Type-specific payload stored as a JSON object.</summary>
    public string Content { get; set; } = "{}";

    public int Position { get; set; }
}
