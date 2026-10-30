using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NotedX.Core.Data;
using NotedX.Core.Storage;

namespace NotedX.Tests;

/// <summary>Isolated SQLite database and app paths under a temporary root.</summary>
internal sealed class TestDatabase : IDbContextFactory<NotedXDbContext>, IAsyncDisposable
{
    private readonly DbContextOptions<NotedXDbContext> _options;

    private TestDatabase(string root)
    {
        Root = root;
        Paths = new AppPaths(root);
        Paths.EnsureDirectories();

        _options = new DbContextOptionsBuilder<NotedXDbContext>()
            .UseSqlite($"Data Source={Paths.DatabaseFilePath};Pooling=False")
            .Options;
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    /// <summary>Creates a migrated database seeded with the default workspace.</summary>
    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase(Path.Combine(
            Path.GetTempPath(),
            "notedx-tests",
            Guid.NewGuid().ToString("N")));

        await DatabaseInitializer.InitializeAsync(database);
        return database;
    }

    public NotedXDbContext CreateDbContext() => new(_options);

    public Task<NotedXDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked temporary directory is harmless.
        }

        return ValueTask.CompletedTask;
    }
}
