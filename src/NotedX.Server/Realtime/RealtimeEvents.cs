namespace NotedX.Server.Realtime;

/// <summary>Broadcast when a page is created, updated, trashed, restored, or deleted.</summary>
/// <param name="PageId">Affected page.</param>
/// <param name="Change">One of <c>created</c>, <c>updated</c>, <c>trashed</c>, <c>restored</c>, or <c>deleted</c>.</param>
/// <param name="At">UTC instant of the change.</param>
public sealed record PageChangedEvent(Guid PageId, string Change, DateTimeOffset At);

/// <summary>Broadcast after each accepted upload chunk.</summary>
public sealed record TransferProgressEvent(
    Guid UploadId,
    string FileName,
    long ReceivedBytes,
    long TotalBytes,
    string DeviceName,
    DateTimeOffset At);

/// <summary>Broadcast when an upload has been stored as an attachment.</summary>
public sealed record TransferCompletedEvent(
    Guid UploadId,
    Guid AttachmentId,
    Guid? PageId,
    string FileName,
    long SizeBytes,
    string ContentHash,
    string DeviceName,
    DateTimeOffset At);
