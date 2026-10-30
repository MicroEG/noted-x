using NotedX.Core.Storage;

namespace NotedX.Server.Transfers;

/// <summary>Metadata for one resumable upload staged under <c>%LOCALAPPDATA%\noted-x\uploads</c>.</summary>
/// <param name="Id">Upload identifier used by the chunk endpoints.</param>
/// <param name="FileName">Sanitized display and download name.</param>
/// <param name="MimeType">Declared content type.</param>
/// <param name="SizeBytes">Total declared size; enforced against every chunk.</param>
/// <param name="PageId">Optional page to attach the finished file to.</param>
/// <param name="DeviceId">Device that started the upload.</param>
/// <param name="DeviceName">Device name recorded on the resulting attachment.</param>
/// <param name="ReceivedBytes">Bytes durably staged so far.</param>
/// <param name="CreatedAt">When the upload was initialized.</param>
/// <param name="CompletedAttachmentId">
/// Set once the attachment row exists, so a retried completion cannot duplicate it.
/// </param>
public sealed record UploadSession(
    Guid Id,
    string FileName,
    string MimeType,
    long SizeBytes,
    Guid? PageId,
    Guid DeviceId,
    string DeviceName,
    long ReceivedBytes,
    DateTimeOffset CreatedAt,
    Guid? CompletedAttachmentId = null);

/// <summary>A completed upload together with its stored blob identity.</summary>
public sealed record UploadCompletion(UploadSession Session, BlobWriteResult Blob);

/// <summary>Result of appending a chunk.</summary>
public enum AppendStatus
{
    /// <summary>The chunk was staged.</summary>
    Success,

    /// <summary>No such upload session exists.</summary>
    NotFound,

    /// <summary>The chunk does not start at the current end of the staged file.</summary>
    OutOfOrder,

    /// <summary>The chunk would exceed the declared size.</summary>
    ExceedsDeclaredSize
}

/// <summary>Outcome of appending one chunk.</summary>
public sealed record AppendChunkResult(AppendStatus Status, UploadSession? Session);

/// <summary>Signals invalid transfer input that is safe to describe to the user.</summary>
public sealed class TransferValidationException : Exception
{
    /// <summary>Creates the exception with a user-facing message.</summary>
    public TransferValidationException(string message)
        : base(message)
    {
    }
}
