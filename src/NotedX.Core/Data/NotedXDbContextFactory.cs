using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NotedX.Core.Storage;

namespace NotedX.Core.Data;

/// <summary>Design-time context factory used by the <c>dotnet ef</c> command line tools.</summary>
public sealed class NotedXDbContextFactory : IDesignTimeDbContextFactory<NotedXDbContext>
{
    /// <inheritdoc />
    public NotedXDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NOTEDX_CONNECTION")
            ?? $"Data Source={AppPaths.CreateDefault().DatabaseFilePath}";

        var options = new DbContextOptionsBuilder<NotedXDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new NotedXDbContext(options);
    }
}
