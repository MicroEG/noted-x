using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NotedX.Core.Storage;

namespace NotedX.Server.Transfers;

/// <summary>Stages chunked uploads on disk so they survive disconnects and app restarts.</summary>
/// <remarks>
/// Each upload owns a <c>{id}.json</c> metadata file and a <c>{id}.part</c> file.
/// The staged byte count is derived from the part file's length, so a crash between
/// writing bytes and updating metadata can only lose timestamps, never payload.
/// </remarks>
public sealed class UploadSessionStore
{
    private const int MaxFileNameLength = 500;
    private const int MaxMimeTypeLength = 255;
    private const long MinimumChunkSize = 64 * 1024;
    private const long MaximumChunkSize = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<char> InvalidFileNameChars = Path.GetInvalidFileNameChars().ToHashSet();

    private readonly string _uploadsDirectory;
    private readonly BlobStore _blobStore;
    private readonly ServerSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UploadSessionStore> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Creates the store over the application's uploads directory.</summary>
    public UploadSessionStore(
        AppPaths paths,
        BlobStore blobStore,
        ServerSettings settings,
        TimeProvider timeProvider,
        ILogger<UploadSessionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _uploadsDirectory = paths.UploadsDirectory;
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Advertised chunk size, clamped to a sane range.</summary>
    public int ChunkSize => (int)Math.Clamp(_settings.ChunkSizeBytes, MinimumChunkSize, MaximumChunkSize);

    /// <summary>Maximum accepted file size.</summary>
    public long MaxUploadBytes => _settings.MaxUploadBytes;

    /// <summary>Creates a new staged upload and returns its session.</summary>
    /// <exception cref="TransferValidationException">The declared size or metadata is invalid.</exception>
    public async Task<UploadSession> CreateAsync(
        string? fileName,
        string? mimeType,
        long sizeBytes,
        Guid? pageId,
        Guid deviceId,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        if (sizeBytes < 0 || sizeBytes > MaxUploadBytes)
        {
            throw new TransferValidationException(
                $"Files must be between 0 and {MaxUploadBytes} bytes.");
        }

        var id = Guid.NewGuid();
        var session = new UploadSession(
            id,
            NormalizeFileName(fileName),
            NormalizeMimeType(mimeType),
            sizeBytes,
            pageId,
            deviceId,
            Truncate(deviceName, 128) ?? "Mobile device",
            0,
            _timeProvider.GetUtcNow());

        Directory.CreateDirectory(_uploadsDirectory);
        var semaphore = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            await using (var part = new FileStream(
                PartPath(id),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await part.FlushAsync(cancellationToken);
            }

            await WriteMetadataAsync(session, cancellationToken);
            _logger.LogInformation(
                "Upload {UploadId} initialized for {SizeBytes} bytes from device {DeviceId}.",
                id,
                sizeBytes,
                deviceId);
            return session;
        }
        catch
        {
            TryDelete(id);
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Loads an upload session and reports how many bytes are durably staged.</summary>
    public async Task<UploadSession?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            return await LoadAsync(id, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Appends a chunk that must begin exactly at the current staged length.</summary>
    public async Task<AppendChunkResult> AppendAsync(
        Guid id,
        long start,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (start < 0 || data.Length == 0)
        {
            throw new TransferValidationException("A chunk needs a non-negative start and at least one byte.");
        }

        if (data.Length > ChunkSize)
        {
            throw new TransferValidationException($"Chunks cannot exceed {ChunkSize} bytes.");
        }

        var semaphore = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var session = await LoadAsync(id, cancellationToken);
            if (session is null)
            {
                return new AppendChunkResult(AppendStatus.NotFound, null);
            }

            if (start != session.ReceivedBytes)
            {
                return new AppendChunkResult(AppendStatus.OutOfOrder, session);
            }

            if (start + data.Length > session.SizeBytes)
            {
                return new AppendChunkResult(AppendStatus.ExceedsDeclaredSize, session);
            }

            await using (var part = new FileStream(
                PartPath(id),
                FileMode.Append,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous))
            {
                await part.WriteAsync(data, cancellationToken);
                await part.FlushAsync(cancellationToken);
            }

            var updated = session with { ReceivedBytes = start + data.Length };
            await WriteMetadataAsync(updated, cancellationToken);
            return new AppendChunkResult(AppendStatus.Success, updated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to append a chunk to upload {UploadId}.", id);
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Stores the fully staged upload in the content-addressed blob store.</summary>
    /// <exception cref="TransferValidationException">The upload is incomplete or missing.</exception>
    public async Task<UploadCompletion> CompleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var session = await LoadAsync(id, cancellationToken)
                ?? throw new TransferValidationException("The upload session was not found.");

            if (session.ReceivedBytes != session.SizeBytes)
            {
                throw new TransferValidationException(
                    $"The upload is incomplete ({session.ReceivedBytes} of {session.SizeBytes} bytes).");
            }

            await using var part = new FileStream(
                PartPath(id),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var blob = await _blobStore.SaveAsync(part, cancellationToken);
            _logger.LogInformation(
                "Upload {UploadId} completed as blob {ContentHash} ({SizeBytes} bytes, deduplicated: {AlreadyExisted}).",
                id,
                blob.ContentHash,
                blob.SizeBytes,
                blob.AlreadyExisted);

            return new UploadCompletion(session, blob);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Records that an attachment row was created for the upload.</summary>
    public async Task MarkCompletedAsync(
        Guid id,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var session = await LoadAsync(id, cancellationToken);
            if (session is not null)
            {
                await WriteMetadataAsync(session with { CompletedAttachmentId = attachmentId }, cancellationToken);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Deletes the staged files for an upload.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            TryDelete(id);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Removes sessions and orphaned part files older than <paramref name="maxAge"/>.</summary>
    public Task CleanupStaleAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_uploadsDirectory))
        {
            return Task.CompletedTask;
        }

        var cutoff = _timeProvider.GetUtcNow() - maxAge;

        foreach (var path in Directory.EnumerateFiles(_uploadsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (lastWrite >= cutoff.UtcDateTime)
                {
                    continue;
                }

                File.Delete(path);
                _logger.LogInformation("Removed stale upload file {FileName}.", Path.GetFileName(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove stale upload file {FileName}.", Path.GetFileName(path));
            }
        }

        return Task.CompletedTask;
    }

    private async Task<UploadSession?> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var metadataPath = MetadataPath(id);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, Encoding.UTF8, cancellationToken);
            var session = JsonSerializer.Deserialize<UploadSession>(json, JsonOptions);
            if (session is null || session.Id != id)
            {
                return null;
            }

            return session with { ReceivedBytes = GetPartLength(id) };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Upload metadata {UploadId} is corrupt and will be ignored.", id);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Upload metadata {UploadId} could not be read.", id);
            return null;
        }
    }

    private async Task WriteMetadataAsync(UploadSession session, CancellationToken cancellationToken)
    {
        var metadataPath = MetadataPath(session.Id);
        var temporaryPath = metadataPath + ".tmp";
        var json = JsonSerializer.Serialize(session, JsonOptions);

        await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken);
        File.Move(temporaryPath, metadataPath, overwrite: true);
    }

    private long GetPartLength(Guid id)
    {
        var partPath = PartPath(id);
        if (!File.Exists(partPath))
        {
            return 0;
        }

        try
        {
            return new FileInfo(partPath).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private string MetadataPath(Guid id) => Path.Combine(_uploadsDirectory, $"{id:N}.json");

    private string PartPath(Guid id) => Path.Combine(_uploadsDirectory, $"{id:N}.part");

    private void TryDelete(Guid id)
    {
        foreach (var path in new[] { MetadataPath(id), PartPath(id), MetadataPath(id) + ".tmp" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete upload file {FileName}.", Path.GetFileName(path));
            }
        }
    }

    private static string NormalizeFileName(string? fileName)
    {
        var raw = (fileName ?? string.Empty).Replace("\0", string.Empty).Trim();
        var name = Path.GetFileName(raw);
        var builder = new StringBuilder(name.Length);

        foreach (var character in name)
        {
            if (char.IsControl(character) || InvalidFileNameChars.Contains(character))
            {
                continue;
            }

            builder.Append(character);
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            return "upload.bin";
        }

        if (cleaned.Length <= MaxFileNameLength)
        {
            return cleaned;
        }

        var extension = Path.GetExtension(cleaned);
        if (extension.Length > 32)
        {
            extension = string.Empty;
        }

        return cleaned[..(MaxFileNameLength - extension.Length)] + extension;
    }

    private static string NormalizeMimeType(string? mimeType)
    {
        var trimmed = mimeType?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            trimmed.Length > MaxMimeTypeLength ||
            trimmed.Any(char.IsControl))
        {
            return "application/octet-stream";
        }

        return trimmed;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
