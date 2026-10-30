using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NotedX.Core.Storage;

/// <summary>Content-addressed blob store backed by the local file system.</summary>
/// <remarks>
/// Files live under <c>blobs/{hash[0..2]}/{hash[2..4]}/{hash}</c>. Writes are staged in
/// <c>blobs/.staging</c> and atomically moved into place, so any visible blob is complete.
/// Identical content is stored once and deduplicated by its SHA-256 hash.
/// </remarks>
public sealed class BlobStore
{
    private const int HashHexLength = 64;
    private const int BufferSize = 81920;

    private readonly string _blobsDirectory;
    private readonly string _stagingDirectory;
    private readonly ILogger<BlobStore> _logger;

    /// <summary>Creates a store over the blob directory described by <paramref name="paths"/>.</summary>
    public BlobStore(AppPaths paths, ILogger<BlobStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _blobsDirectory = paths.BlobsDirectory;
        _stagingDirectory = Path.Combine(_blobsDirectory, ".staging");
        _logger = logger;
    }

    /// <summary>
    /// Reads <paramref name="source"/> to its end, stores it under its lower-case SHA-256 hash,
    /// and reports whether identical content was already present.
    /// </summary>
    /// <param name="source">Readable stream whose current position is the start of the content.</param>
    /// <param name="cancellationToken">Cancels the copy and removes the staging file.</param>
    public async Task<BlobWriteResult> SaveAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        var stagingPath = Path.Combine(_stagingDirectory, $"{Guid.NewGuid():N}.tmp");
        long sizeBytes = 0;

        try
        {
            Directory.CreateDirectory(_stagingDirectory);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var destination = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int bytesRead;
                    while ((bytesRead = await source
                        .ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                        .ConfigureAwait(false)) > 0)
                    {
                        hasher.AppendData(buffer, 0, bytesRead);
                        await destination
                            .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                            .ConfigureAwait(false);
                        sizeBytes += bytesRead;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var contentHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            var finalPath = GetBlobPath(contentHash);

            if (File.Exists(finalPath))
            {
                TryDelete(stagingPath);
                _logger.LogDebug(
                    "Blob {ContentHash} already exists; deduplicated {SizeBytes} bytes.",
                    contentHash,
                    sizeBytes);
                return new BlobWriteResult(contentHash, sizeBytes, AlreadyExisted: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            try
            {
                File.Move(stagingPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                TryDelete(stagingPath);
                _logger.LogDebug(
                    "Blob {ContentHash} was stored concurrently; deduplicated {SizeBytes} bytes.",
                    contentHash,
                    sizeBytes);
                return new BlobWriteResult(contentHash, sizeBytes, AlreadyExisted: true);
            }

            _logger.LogDebug("Stored blob {ContentHash} ({SizeBytes} bytes).", contentHash, sizeBytes);
            return new BlobWriteResult(contentHash, sizeBytes, AlreadyExisted: false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(stagingPath);
            _logger.LogDebug("Blob write was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(stagingPath);
            _logger.LogError(ex, "Failed to store a blob after writing {SizeBytes} bytes.", sizeBytes);
            throw;
        }
    }

    /// <summary>
    /// Opens a bounded, seekable read window over a stored blob.
    /// </summary>
    /// <param name="contentHash">64-character SHA-256 hash; case-insensitive.</param>
    /// <param name="offset">Zero-based byte offset at which the window starts.</param>
    /// <param name="count">
    /// Number of bytes to expose. Values beyond the end are clamped; <c>null</c> reads to the end.
    /// </param>
    /// <returns>A disposable read window, or <c>null</c> when the blob does not exist.</returns>
    /// <exception cref="ArgumentException">The hash is not a valid SHA-256 hex string.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The offset or count is negative, or the offset lies beyond the end of the blob.
    /// </exception>
    public BlobRead? OpenRead(string contentHash, long offset = 0, long? count = null)
    {
        var normalizedHash = NormalizeHash(contentHash);

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The offset cannot be negative.");
        }

        if (count is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The count cannot be negative.");
        }

        var blobPath = GetBlobPath(normalizedHash);
        FileStream stream;

        try
        {
            stream = new FileStream(
                blobPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open blob {ContentHash}.", normalizedHash);
            throw;
        }

        try
        {
            var totalLength = stream.Length;
            if (offset > totalLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    "The offset lies beyond the end of the blob.");
            }

            var length = count.HasValue
                ? Math.Min(count.Value, totalLength - offset)
                : totalLength - offset;

            if (offset > 0)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }

            return new BlobRead(
                new BoundedReadStream(stream, offset, length),
                offset,
                length,
                totalLength);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private string GetBlobPath(string normalizedHash) =>
        Path.Combine(
            _blobsDirectory,
            normalizedHash[..2],
            normalizedHash[2..4],
            normalizedHash);

    private static string NormalizeHash(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash) ||
            contentHash.Length != HashHexLength ||
            !contentHash.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException(
                "A content hash must be a 64-character SHA-256 hexadecimal string.",
                nameof(contentHash));
        }

        return contentHash.ToLowerInvariant();
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the staging file {StagingPath}.", path);
        }
    }
}
