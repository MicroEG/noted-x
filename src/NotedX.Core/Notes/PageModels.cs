using NotedX.Core.Domain;

namespace NotedX.Core.Notes;

/// <summary>Input for creating or replacing a block through the page service.</summary>
/// <param name="Type">Kind of block.</param>
/// <param name="Content">JSON object payload understood by the editor for this block type.</param>
public sealed record BlockDraft(BlockType Type, string Content);

/// <summary>Input for creating a new page.</summary>
public sealed record PageDraft(
    Guid? WorkspaceId,
    Guid? ParentId,
    string? Title,
    string? Icon,
    IReadOnlyList<BlockDraft>? Blocks);

/// <summary>Input for updating a page. Null members leave the existing value untouched.</summary>
public sealed record PageChanges(
    string? Title,
    string? Icon,
    bool? IsFavorite,
    IReadOnlyList<BlockDraft>? Blocks,
    IReadOnlyList<string>? Tags);

/// <summary>A single full-text search result.</summary>
public sealed record PageSearchHit(
    Guid Id,
    string Title,
    string? Icon,
    string Snippet,
    DateTimeOffset UpdatedAt);
