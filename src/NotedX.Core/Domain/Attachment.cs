namespace NotedX.Core.Domain;

/// <summary>A file attached to a page, or kept in the workspace file browser when no page is set.</summary>
public sealed class Attachment
{
    public Guid Id { get; set; }

    public Guid? PageId { get; set; }

    public Page? Page { get; set; }

    /// <summary>Lower-case hexadecimal SHA-256 hash of the blob contents.</summary>
    public string BlobHash { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Origin of the upload: <c>desktop</c> or the name of a paired device.</summary>
    public string UploadedBy { get; set; } = "desktop";

    public DateTimeOffset CreatedAt { get; set; }
}
