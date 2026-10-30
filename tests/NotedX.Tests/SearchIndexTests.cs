using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NotedX.Core.Data;
using NotedX.Core.Domain;

namespace NotedX.Tests;

public sealed class SearchIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "notedx-tests",
        Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_root, "notedx.db");

    [Fact]
    public async Task Migration_creates_and_maintains_the_full_text_index()
    {
        Directory.CreateDirectory(_root);

        var options = new DbContextOptionsBuilder<NotedXDbContext>()
            .UseSqlite($"Data Source={DatabasePath};Pooling=False")
            .Options;

        await using var context = new NotedXDbContext(options);
        await context.Database.MigrateAsync();

        var workspaceId = Guid.NewGuid();
        var page = new Page
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Title = "Garden plans",
            SearchText = "Tomatoes, basil and a small greenhouse.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        context.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Personal",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Pages.Add(page);
        await context.SaveChangesAsync();

        Assert.Equal(1, await CountMatchesAsync(context, "greenhouse"));

        page.Title = "Balcony garden";
        page.SearchText = "Cherry tomatoes and peppers in pots.";
        await context.SaveChangesAsync();

        Assert.Equal(1, await CountMatchesAsync(context, "peppers"));
        Assert.Equal(0, await CountMatchesAsync(context, "greenhouse"));

        context.Pages.Remove(page);
        await context.SaveChangesAsync();

        Assert.Equal(0, await CountMatchesAsync(context, "peppers"));
    }

    private static Task<int> CountMatchesAsync(NotedXDbContext context, string term) =>
        context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS \"Value\" FROM \"PageSearch\" WHERE \"PageSearch\" MATCH {0}",
                term)
            .SingleAsync();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
