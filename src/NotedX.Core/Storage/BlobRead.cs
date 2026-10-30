namespace NotedX.Core.Storage;

/// <summary>A bounded read window over a stored blob.</summary>
/// <param name="Content">Seekable stream limited to the requested window.</param>
/// <param name="Offset">Absolute byte offset of the window within the blob.</param>
/// <param name="Length">Number of bytes exposed by the window.</param>
/// <param name="TotalLength">Full size of the blob in bytes.</param>
public sealed record BlobRead(Stream Content, long Offset, long Length, long TotalLength) : IDisposable
{
    /// <summary>Disposes the content stream.</summary>
    public void Dispose() => Content.Dispose();
}
