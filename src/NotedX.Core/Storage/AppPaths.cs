namespace NotedX.Core.Storage;

/// <summary>
/// Well-known file system locations owned by noted-x. Every file the
/// application creates lives underneath <see cref="Root"/>.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Folder name created under the current user's local application data directory.</summary>
    public const string FolderName = "noted-x";

    /// <summary>Creates a path set rooted at an explicit directory.</summary>
    /// <param name="rootDirectory">Absolute or relative directory that becomes <see cref="Root"/>.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rootDirectory"/> is blank.</exception>
    public AppPaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
        }

        Root = Path.GetFullPath(rootDirectory);
    }

    /// <summary>Root directory for all application data.</summary>
    public string Root { get; }

    /// <summary>SQLite database file.</summary>
    public string DatabaseFilePath => Path.Combine(Root, "notedx.db");

    /// <summary>Content-addressed blob store (SHA-256 file names).</summary>
    public string BlobsDirectory => Path.Combine(Root, "blobs");

    /// <summary>Staging area for resumable, in-progress uploads.</summary>
    public string UploadsDirectory => Path.Combine(Root, "uploads");

    /// <summary>Self-signed HTTPS certificate and key files.</summary>
    public string CertificatesDirectory => Path.Combine(Root, "certs");

    /// <summary>Rolling Serilog log files.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Creates the <c>%LOCALAPPDATA%\noted-x</c> path set.</summary>
    public static AppPaths CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FolderName));

    /// <summary>Creates every directory the application writes to. Safe to call repeatedly.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BlobsDirectory);
        Directory.CreateDirectory(UploadsDirectory);
        Directory.CreateDirectory(CertificatesDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <inheritdoc />
    public override string ToString() => Root;
}
