using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotedX.Core.Data;
using NotedX.Core.Domain;
using NotedX.Core.Storage;

namespace NotedX.Core.Notes;

/// <summary>Imports local files into the blob store and manages their attachment records.</summary>
public sealed class AttachmentService
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".heic"] = "image/heic",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".html"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "text/javascript",
        [".zip"] = "application/zip",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime"
    };

    private readonly IDbContextFactory<NotedXDbContext> _contextFactory;
    private readonly BlobStore _blobs;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AttachmentService>? _logger;

    /// <summary>Creates the service over the shared database and blob store.</summary>
    public AttachmentService(
        IDbContextFactory<NotedXDbContext> contextFactory,
        BlobStore blobs,
        TimeProvider? timeProvider = null,
        ILogger<AttachmentService>? logger = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Copies a local file into the content-addressed store and links it to an optional page.</summary>
    /// <exception cref="FileNotFoundException">The source file does not exist.</exception>
    /// <exception cref="PageValidationException">The target page does not exist.</exception>
    public async Task<Attachment> ImportAsync(
        string sourcePath,
        Guid? pageId = null,
        string uploadedBy = "desktop",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("A file path is required.", nameof(sourcePath));
        }

        var fullPath = Path.GetFullPath(sourcePath);
        var fileName = Path.GetFileName(fullPath);

        if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected file no longer exists.", fullPath);
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        if (pageId is { } target)
        {
            var exists = await db.Pages
                .AnyAsync(page => page.Id == target && !page.IsDeleted, cancellationToken);

            if (!exists)
            {
                throw new PageValidationException("The target page was not found.");
            }
        }

        BlobWriteResult stored;
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            stored = await _blobs.SaveAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Failed to import attachment {FileName}.", fileName);
            throw;
        }

        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            PageId = pageId,
            BlobHash = stored.ContentHash,
            FileName = fileName,
            MimeType = GuessMimeType(fileName),
            SizeBytes = stored.SizeBytes,
            UploadedBy = NormalizeUploader(uploadedBy),
            CreatedAt = _timeProvider.GetUtcNow()
        };

        try
        {
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to record attachment {FileName}.", fileName);
            throw;
        }

        _logger?.LogInformation(
            "Imported attachment {AttachmentId} ({SizeBytes} bytes{Duplicated}).",
            attachment.Id,
            attachment.SizeBytes,
            stored.AlreadyExisted ? ", deduplicated" : string.Empty);

        return attachment;
    }

    /// <summary>Lists every attachment, newest first.</summary>
    public async Task<IReadOnlyList<Attachment>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var attachments = await db.Attachments.AsNoTracking().ToListAsync(cancellationToken);
        return Order(attachments);
    }

    /// <summary>Lists the attachments of a page, newest first.</summary>
    public async Task<IReadOnlyList<Attachment>> ListForPageAsync(
        Guid pageId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var attachments = await db.Attachments
            .AsNoTracking()
            .Where(attachment => attachment.PageId == pageId)
            .ToListAsync(cancellationToken);

        return Order(attachments);
    }

    /// <summary>Loads a single attachment record.</summary>
    public async Task<Attachment?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Attachments
            .AsNoTracking()
            .FirstOrDefaultAsync(attachment => attachment.Id == id, cancellationToken);
    }

    /// <summary>Opens the stored content of an attachment, or <c>null</c> when its blob is missing.</summary>
    public BlobRead? OpenRead(Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return _blobs.OpenRead(attachment.BlobHash);
    }

    /// <summary>Removes an attachment record. Blobs are content addressed and shared, so they are kept.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (attachment is null)
        {
            return false;
        }

        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);
        _logger?.LogInformation("Removed attachment {AttachmentId}.", id);
        return true;
    }

    /// <summary>Guesses a MIME type from a file name extension.</summary>
    public static string GuessMimeType(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "application/octet-stream";
        }

        var extension = Path.GetExtension(fileName);
        return MimeTypes.TryGetValue(extension, out var mimeType)
            ? mimeType
            : "application/octet-stream";
    }

    private static IReadOnlyList<Attachment> Order(IEnumerable<Attachment> attachments) =>
        attachments
            .OrderByDescending(attachment => attachment.CreatedAt)
            .ThenBy(attachment => attachment.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeUploader(string? uploadedBy)
    {
        if (string.IsNullOrWhiteSpace(uploadedBy))
        {
            return "desktop";
        }

        var trimmed = uploadedBy.Trim();
        return trimmed.Length <= 100 ? trimmed : trimmed[..100];
    }
}
