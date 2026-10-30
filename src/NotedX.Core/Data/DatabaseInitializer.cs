using Microsoft.EntityFrameworkCore;
using NotedX.Core.Domain;

namespace NotedX.Core.Data;

/// <summary>Applies migrations and seeds the minimum data required by a fresh installation.</summary>
public static class DatabaseInitializer
{
    /// <summary>Migrates the database, enables WAL, and creates the first workspace when needed.</summary>
    public static async Task InitializeAsync(
        IDbContextFactory<NotedXDbContext> contextFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);

        // WAL lets the UI read while the embedded server writes.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        if (await db.Workspaces.AnyAsync(cancellationToken))
        {
            return;
        }

        db.Workspaces.Add(new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "My Workspace",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
