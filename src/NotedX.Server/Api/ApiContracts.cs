namespace NotedX.Server.Api;

/// <summary>Body of <c>POST /pair</c>.</summary>
public sealed record PairRequest(string? Token, string? Pin, string? DeviceName);

/// <summary>Response of a successful pairing exchange.</summary>
public sealed record PairResponse(Guid DeviceId, string DeviceName, string Token);

/// <summary>A block supplied by a mobile client.</summary>
public sealed record BlockInput(Guid? Id, string? Type, string? Content);

/// <summary>A block returned to clients.</summary>
public sealed record BlockDto(Guid Id, string Type, string Content, int Position);

/// <summary>A tag applied to a page.</summary>
public sealed record TagDto(Guid Id, string Name, string? Color);

/// <summary>An attachment stored in the blob store.</summary>
public sealed record AttachmentDto(
    Guid Id,
    Guid? PageId,
    string FileName,
    string MimeType,
    long SizeBytes,
    string UploadedBy,
    DateTimeOffset CreatedAt,
    string ContentHash,
    string DownloadUrl);

/// <summary>Sidebar/list representation of a page.</summary>
public sealed record PageSummaryDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? ParentId,
    string Title,
    string? Icon,
    double Position,
    bool IsFavorite,
    bool IsDeleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A page with its ordered blocks, tags, and attachments.</summary>
public sealed record PageDetailDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? ParentId,
    string Title,
    string? Icon,
    double Position,
    bool IsFavorite,
    bool IsDeleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<BlockDto> Blocks,
    IReadOnlyList<TagDto> Tags,
    IReadOnlyList<AttachmentDto> Attachments);

/// <summary>Body of <c>POST /api/pages</c>.</summary>
public sealed record CreatePageRequest(
    Guid? WorkspaceId,
    Guid? ParentId,
    string? Title,
    string? Icon,
    IReadOnlyList<BlockInput>? Blocks);

/// <summary>Body of <c>PUT /api/pages/{id}</c>. Null members keep their current value.</summary>
public sealed record UpdatePageRequest(
    string? Title,
    string? Icon,
    bool? IsFavorite,
    IReadOnlyList<BlockInput>? Blocks,
    IReadOnlyList<string>? Tags);

/// <summary>Body of <c>PUT /api/pages/{id}/move</c>.</summary>
public sealed record MovePageRequest(Guid? ParentId, double? Position);

/// <summary>A workspace returned to clients.</summary>
public sealed record WorkspaceDto(Guid Id, string Name, DateTimeOffset CreatedAt);

/// <summary>A tag with the number of live pages that use it.</summary>
public sealed record TagSummaryDto(Guid Id, string Name, string? Color, int PageCount);

/// <summary>Metadata about a paired device. Token hashes are never returned.</summary>
public sealed record DeviceDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool IsRevoked);

/// <summary>A stored page revision.</summary>
public sealed record RevisionDto(Guid Id, DateTimeOffset CreatedAt, string ContentSnapshot);

/// <summary>Body of <c>POST /api/files</c>, which initializes a resumable upload.</summary>
public sealed record CreateUploadRequest(
    string? FileName,
    string? MimeType,
    long? SizeBytes,
    Guid? PageId);

/// <summary>Response describing how to upload a file in chunks.</summary>
public sealed record UploadInitResponse(
    Guid UploadId,
    int ChunkSize,
    long SizeBytes,
    long ReceivedBytes,
    string UploadUrl);

/// <summary>Current progress of a staged upload, used to resume after a disconnect.</summary>
public sealed record UploadStatusResponse(
    Guid UploadId,
    string FileName,
    string MimeType,
    long SizeBytes,
    long ReceivedBytes,
    Guid? PageId,
    bool CanComplete,
    Guid? CompletedAttachmentId = null);

/// <summary>Result of one chunk upload or a completion request.</summary>
public sealed record UploadChunkResponse(
    Guid UploadId,
    long ReceivedBytes,
    long SizeBytes,
    bool Completed,
    Guid? AttachmentId,
    string? ContentHash);
