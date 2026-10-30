using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using NotedX.Core.Data;
using NotedX.Core.Domain;
using NotedX.Core.Storage;
using NotedX.Server.Realtime;
using NotedX.Server.Security;
using NotedX.Server.Transfers;

namespace NotedX.Server.Api;

/// <summary>Marker type used only to categorize transfer endpoint logs.</summary>
internal sealed class FileApiLog
{
}

internal static class FileEndpoints
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> FinalizeGates = new();

    public static void MapFileEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/files", InitAsync);
        api.MapPut("/files/{uploadId:guid}", UploadChunkAsync);
        api.MapGet("/files", ListAsync);
        api.MapGet("/files/{id:guid}", DownloadAsync);
        api.MapGet("/files/{id:guid}/meta", MetaAsync);
        api.MapDelete("/files/{id:guid}", DeleteAsync);
        api.MapGet("/uploads/{uploadId:guid}", StatusAsync);
        api.MapPost("/uploads/{uploadId:guid}/complete", CompleteAsync);
        api.MapDelete("/uploads/{uploadId:guid}", CancelAsync);
    }

    private static async Task<IResult> InitAsync(
        CreateUploadRequest request,
        HttpContext http,
        UploadSessionStore uploads,
        IDbContextFactory<NotedXDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        if (!TryGetDevice(http, out var deviceId, out var deviceName))
        {
            return ApiResults.Unauthorized("This device is not paired.");
        }

        if (request.SizeBytes is not { } sizeBytes)
        {
            return ApiResults.BadRequest("sizeBytes is required.");
        }

        if (request.PageId is { } pageId)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var pageExists = await db.Pages
                .AnyAsync(page => page.Id == pageId && !page.IsDeleted, cancellationToken);

            if (!pageExists)
            {
                return ApiResults.BadRequest("The target page was not found.");
            }
        }

        try
        {
            var session = await uploads.CreateAsync(
                request.FileName,
                request.MimeType,
                sizeBytes,
                request.PageId,
                deviceId,
                deviceName,
                cancellationToken);

            return Results.Created(
                $"/api/uploads/{session.Id}",
                new UploadInitResponse(
                    session.Id,
                    uploads.ChunkSize,
                    session.SizeBytes,
                    session.ReceivedBytes,
                    $"/api/files/{session.Id}"));
        }
        catch (TransferValidationException ex)
        {
            return ApiResults.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> UploadChunkAsync(
        Guid uploadId,
        HttpContext http,
        UploadSessionStore uploads,
        IDbContextFactory<NotedXDbContext> contextFactory,
        RealtimeNotifier notifier,
        TimeProvider timeProvider,
        ILogger<FileApiLog> logger,
        CancellationToken cancellationToken)
    {
        if (!TryGetDevice(http, out var deviceId, out _))
        {
            return ApiResults.Unauthorized("This device is not paired.");
        }

        var session = await uploads.GetAsync(uploadId, cancellationToken);
        if (session is null)
        {
            return ApiResults.NotFound("The upload session was not found.");
        }

        if (session.DeviceId != deviceId)
        {
            return ApiResults.Forbidden("This upload belongs to another device.");
        }

        if (session.CompletedAttachmentId is { } completedId)
        {
            return Results.Ok(new UploadChunkResponse(
                session.Id,
                session.ReceivedBytes,
                session.SizeBytes,
                true,
                completedId,
                null));
        }

        if (!TryParseContentRange(http.Request.Headers.ContentRange.ToString(), out var from, out var to, out var total))
        {
            return ApiResults.BadRequest("A Content-Range header such as 'bytes 0-1048575/5242880' is required.");
        }

        if (total != session.SizeBytes)
        {
            return ApiResults.BadRequest("The Content-Range total does not match the initialized upload size.");
        }

        if (from != session.ReceivedBytes)
        {
            return Conflict(session);
        }

        if (to < from || to >= session.SizeBytes)
        {
            return ApiResults.BadRequest("The Content-Range is outside the declared file size.");
        }

        var chunkLength = checked((int)(to - from + 1));
        if (chunkLength > uploads.ChunkSize)
        {
            return ApiResults.BadRequest($"Chunks cannot exceed {uploads.ChunkSize} bytes.");
        }

        if (http.Request.ContentLength is { } contentLength && contentLength != chunkLength)
        {
            return ApiResults.BadRequest("Content-Length does not match the Content-Range.");
        }

        var buffer = new byte[chunkLength];
        try
        {
            await http.Request.Body.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return ApiResults.BadRequest("The request body ended before the chunk was complete.");
        }

        var append = await uploads.AppendAsync(uploadId, from, buffer, cancellationToken);
        switch (append.Status)
        {
            case AppendStatus.NotFound:
                return ApiResults.NotFound("The upload session was not found.");

            case AppendStatus.OutOfOrder when append.Session is not null:
                return Conflict(append.Session);

            case AppendStatus.ExceedsDeclaredSize:
                return ApiResults.BadRequest("The chunk would exceed the declared file size.");

            case AppendStatus.Success when append.Session is not null:
                break;

            default:
                return ApiResults.Conflict("The chunk could not be staged.");
        }

        var updated = append.Session!;
        await notifier.TransferProgressAsync(
            updated.Id,
            updated.FileName,
            updated.ReceivedBytes,
            updated.SizeBytes,
            updated.DeviceName,
            cancellationToken);

        if (updated.ReceivedBytes >= updated.SizeBytes)
        {
            return await FinalizeAsync(
                uploadId,
                uploads,
                contextFactory,
                notifier,
                timeProvider,
                logger,
                cancellationToken);
        }

        return Results.Ok(new UploadChunkResponse(
            updated.Id,
            updated.ReceivedBytes,
            updated.SizeBytes,
            Completed: false,
            AttachmentId: null,
            ContentHash: null));
    }

    private static async Task<IResult> CompleteAsync(
        Guid uploadId,
        HttpContext http,
        UploadSessionStore uploads,
        IDbContextFactory<NotedXDbContext> contextFactory,
        RealtimeNotifier notifier,
        TimeProvider timeProvider,
        ILogger<FileApiLog> logger,
        CancellationToken cancellationToken)
    {
        if (!TryGetDevice(http, out var deviceId, out _))
        {
            return ApiResults.Unauthorized("This device is not paired.");
        }

        var session = await uploads.GetAsync(uploadId, cancellationToken);
        if (session is null)
        {
            return ApiResults.NotFound("The upload session was not found.");
        }

        if (session.DeviceId != deviceId)
        {
            return ApiResults.Forbidden("This upload belongs to another device.");
        }

        if (session.ReceivedBytes != session.SizeBytes)
        {
            return Conflict(session);
        }

        return await FinalizeAsync(
            uploadId,
            uploads,
            contextFactory,
            notifier,
            timeProvider,
            logger,
            cancellationToken);
    }

    private static async Task<IResult> FinalizeAsync(
        Guid uploadId,
        UploadSessionStore uploads,
        IDbContextFactory<NotedXDbContext> contextFactory,
        RealtimeNotifier notifier,
        TimeProvider timeProvider,
        ILogger<FileApiLog> logger,
        CancellationToken cancellationToken)
    {
        var gate = FinalizeGates.GetOrAdd(uploadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            var session = await uploads.GetAsync(uploadId, cancellationToken);
            if (session is null)
            {
                return ApiResults.NotFound("The upload session was not found or has already completed.");
            }

            if (session.CompletedAttachmentId is { } completedId)
            {
                await uploads.RemoveAsync(uploadId, cancellationToken);
                return Results.Ok(new UploadChunkResponse(
                    session.Id,
                    session.ReceivedBytes,
                    session.SizeBytes,
                    true,
                    completedId,
                    null));
            }

            UploadCompletion completion;
            try
            {
                completion = await uploads.CompleteAsync(uploadId, cancellationToken);
            }
            catch (TransferValidationException ex)
            {
                logger.LogWarning("Upload {UploadId} could not be completed: {Reason}", uploadId, ex.Message);
                return Conflict(session);
            }

            Guid? pageId = session.PageId;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

            if (pageId is { } candidate &&
                !await db.Pages.AnyAsync(page => page.Id == candidate && !page.IsDeleted, cancellationToken))
            {
                pageId = null;
            }

            var attachment = new Attachment
            {
                Id = Guid.NewGuid(),
                PageId = pageId,
                BlobHash = completion.Blob.ContentHash,
                FileName = session.FileName,
                MimeType = session.MimeType,
                SizeBytes = completion.Blob.SizeBytes,
                UploadedBy = session.DeviceName,
                CreatedAt = timeProvider.GetUtcNow()
            };

            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                await uploads.MarkCompletedAsync(uploadId, attachment.Id, cancellationToken);
                await uploads.RemoveAsync(uploadId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not clean up the staged files for upload {UploadId}.", uploadId);
            }

            await notifier.TransferCompletedAsync(
                session.Id,
                attachment.Id,
                pageId,
                session.FileName,
                attachment.SizeBytes,
                attachment.BlobHash,
                session.DeviceName,
                CancellationToken.None);

            return Results.Ok(new UploadChunkResponse(
                session.Id,
                session.ReceivedBytes,
                session.SizeBytes,
                true,
                attachment.Id,
                attachment.BlobHash));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<IResult> StatusAsync(
        Guid uploadId,
        HttpContext http,
        UploadSessionStore uploads,
        CancellationToken cancellationToken)
    {
        if (!TryGetDevice(http, out var deviceId, out _))
        {
            return ApiResults.Unauthorized("This device is not paired.");
        }

        var session = await uploads.GetAsync(uploadId, cancellationToken);
        if (session is null)
        {
            return ApiResults.NotFound("The upload session was not found.");
        }

        if (session.DeviceId != deviceId)
        {
            return ApiResults.Forbidden("This upload belongs to another device.");
        }

        return Results.Ok(ToStatus(session));
    }

    private static async Task<IResult> CancelAsync(
        Guid uploadId,
        HttpContext http,
        UploadSessionStore uploads,
        CancellationToken cancellationToken)
    {
        if (!TryGetDevice(http, out var deviceId, out _))
        {
            return ApiResults.Unauthorized("This device is not paired.");
        }

        var session = await uploads.GetAsync(uploadId, cancellationToken);
        if (session is null)
        {
            return ApiResults.NotFound("The upload session was not found.");
        }

        if (session.DeviceId != deviceId)
        {
            return ApiResults.Forbidden("This upload belongs to another device.");
        }

        await uploads.RemoveAsync(uploadId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(
        Guid? pageId,
        IDbContextFactory<NotedXDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Attachments.AsNoTracking();

        if (pageId is { } page)
        {
            query = query.Where(attachment => attachment.PageId == page);
        }

        var attachments = await query
            .ToListAsync(cancellationToken);

        return Results.Ok(attachments
            .OrderByDescending(attachment => attachment.CreatedAt)
            .Select(PageContractMapper.ToAttachment)
            .ToArray());
    }

    private static async Task<IResult> MetaAsync(
        Guid id,
        IDbContextFactory<NotedXDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var attachment = await db.Attachments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        return attachment is null
            ? ApiResults.NotFound("That file was not found.")
            : Results.Ok(PageContractMapper.ToAttachment(attachment));
    }

    private static async Task<IResult> DownloadAsync(
        Guid id,
        IDbContextFactory<NotedXDbContext> contextFactory,
        BlobStore blobs,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var attachment = await db.Attachments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (attachment is null)
        {
            return ApiResults.NotFound("That file was not found.");
        }

        var blob = blobs.OpenRead(attachment.BlobHash);
        if (blob is null)
        {
            return ApiResults.NotFound("The stored file is missing.");
        }

        var contentType = string.IsNullOrWhiteSpace(attachment.MimeType)
            ? "application/octet-stream"
            : attachment.MimeType;

        return Results.File(
            blob.Content,
            contentType,
            attachment.FileName,
            lastModified: attachment.CreatedAt,
            entityTag: new EntityTagHeaderValue($"\"{attachment.BlobHash}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        IDbContextFactory<NotedXDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var attachment = await db.Attachments.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (attachment is null)
        {
            return ApiResults.NotFound("That file was not found.");
        }

        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static IResult Conflict(UploadSession session) =>
        Results.Json(ToStatus(session), statusCode: StatusCodes.Status409Conflict);

    private static UploadStatusResponse ToStatus(UploadSession session) => new(
        session.Id,
        session.FileName,
        session.MimeType,
        session.SizeBytes,
        session.ReceivedBytes,
        session.PageId,
        session.ReceivedBytes >= session.SizeBytes,
        session.CompletedAttachmentId);

    private static bool TryGetDevice(HttpContext http, out Guid deviceId, out string deviceName)
    {
        deviceName = http.User.FindFirstValue(DeviceClaims.DeviceName) ?? "Mobile device";
        return Guid.TryParse(http.User.FindFirstValue(DeviceClaims.DeviceId), out deviceId);
    }

    private static bool TryParseContentRange(string? header, out long from, out long to, out long total)
    {
        from = 0;
        to = 0;
        total = 0;

        if (!ContentRangeHeaderValue.TryParse(header, out var range) ||
            !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
            range.From is not { } start ||
            range.To is not { } end ||
            range.Length is not { } length)
        {
            return false;
        }

        from = start;
        to = end;
        total = length;
        return true;
    }
}
