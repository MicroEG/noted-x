namespace NotedX.Core.Storage;

/// <summary>Outcome of storing content in the <see cref="BlobStore"/>.</summary>
/// <param name="ContentHash">Lower-case hexadecimal SHA-256 identity of the content.</param>
/// <param name="SizeBytes">Number of bytes stored.</param>
/// <param name="AlreadyExisted">True when identical content was already present.</param>
public sealed record BlobWriteResult(string ContentHash, long SizeBytes, bool AlreadyExisted);
