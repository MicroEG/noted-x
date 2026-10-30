using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using NotedX.Core.Domain;
using NotedX.Core.Notes;
using NotedX.Core.Storage;

namespace NotedX.Desktop.Services;

/// <summary>Opens attachments outside the app and decodes image previews for the editor.</summary>
public sealed class AttachmentPreviewService
{
    private static readonly HashSet<string> PreviewableImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/bmp",
        "image/tiff"
    };

    private readonly AttachmentService _attachments;
    private readonly ILogger<AttachmentPreviewService> _logger;

    /// <summary>Creates the service over the local blob store.</summary>
    public AttachmentPreviewService(
        AttachmentService attachments,
        ILogger<AttachmentPreviewService> logger)
    {
        _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether the attachment can be shown inline as an image.</summary>
    public static bool CanPreviewImage(Attachment attachment) =>
        attachment is not null && PreviewableImageTypes.Contains(attachment.MimeType);

    /// <summary>Decodes the attachment into a frozen image sized for display.</summary>
    public async Task<BitmapImage?> LoadImageAsync(
        Attachment attachment,
        int decodePixelWidth = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        BlobRead? read;
        try
        {
            read = _attachments.OpenRead(attachment);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the blob for attachment {AttachmentId}.", attachment.Id);
            return null;
        }

        if (read is null)
        {
            _logger.LogWarning("The blob for attachment {AttachmentId} is missing.", attachment.Id);
            return null;
        }

        using (read)
        {
            using var buffer = new MemoryStream();
            await read.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return null;
            }

            return await dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    image.DecodePixelWidth = Math.Max(64, decodePixelWidth);
                    image.StreamSource = buffer;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not decode image attachment {AttachmentId}.", attachment.Id);
                    return null;
                }
            });
        }
    }

    /// <summary>Copies the attachment to a temporary folder and opens it with its default app.</summary>
    /// <returns>The opened file path.</returns>
    /// <exception cref="FileNotFoundException">The stored blob is missing.</exception>
    public async Task<string> OpenAsync(Attachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var directory = Path.Combine(
            Path.GetTempPath(),
            "noted-x",
            "preview",
            attachment.Id.ToString("N"));

        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, MakeSafeFileName(attachment));

        var needsCopy = true;
        if (File.Exists(targetPath))
        {
            needsCopy = new FileInfo(targetPath).Length != attachment.SizeBytes;
        }

        if (needsCopy)
        {
            BlobRead? read;
            try
            {
                read = _attachments.OpenRead(attachment);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not open attachment {AttachmentId}.", attachment.Id);
                throw new FileNotFoundException("The stored file could not be opened.", targetPath, ex);
            }

            if (read is null)
            {
                throw new FileNotFoundException("The stored file is missing.", targetPath);
            }

            using (read)
            {
                await using var destination = new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 81920,
                    FileOptions.Asynchronous);

                await read.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open attachment {AttachmentId} with the shell.", attachment.Id);
            throw;
        }

        return targetPath;
    }

    /// <summary>Removes temporary preview copies older than a day. Failures are logged and ignored.</summary>
    public void CleanUpTemporaryFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "noted-x", "preview");

        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not remove the temporary attachment folder {Folder}.", directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not clean up temporary attachment previews.");
        }
    }

    private static string MakeSafeFileName(Attachment attachment)
    {
        var name = Path.GetFileName(attachment.FileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return attachment.Id.ToString("N") + ".bin";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.Length == 0 ? attachment.Id.ToString("N") + ".bin" : builder.ToString();
    }
}
