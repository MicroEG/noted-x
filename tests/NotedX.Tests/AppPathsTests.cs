using NotedX.Core.Storage;

namespace NotedX.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "notedx-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureDirectories_creates_the_owned_layout_idempotently()
    {
        var paths = new AppPaths(_root);

        paths.EnsureDirectories();
        paths.EnsureDirectories();

        Assert.True(Directory.Exists(paths.Root));
        Assert.True(Directory.Exists(paths.BlobsDirectory));
        Assert.True(Directory.Exists(paths.UploadsDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void Database_file_is_placed_under_the_root()
    {
        var paths = new AppPaths(_root);

        Assert.Equal(Path.Combine(paths.Root, "notedx.db"), paths.DatabaseFilePath);
    }

    [Fact]
    public void Constructor_rejects_a_blank_root()
    {
        Assert.Throws<ArgumentException>(() => new AppPaths("   "));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
