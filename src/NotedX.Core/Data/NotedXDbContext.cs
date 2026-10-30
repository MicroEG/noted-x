using Microsoft.EntityFrameworkCore;
using NotedX.Core.Domain;

namespace NotedX.Core.Data;

/// <summary>EF Core context for the local SQLite database.</summary>
/// <remarks>
/// The FTS5 <c>PageSearch</c> virtual table and its synchronization triggers are created
/// by migrations rather than mapped here, because SQLite virtual tables cannot be
/// expressed in the EF Core model.
/// </remarks>
public sealed class NotedXDbContext : DbContext
{
    /// <summary>Creates a context with the supplied provider options.</summary>
    public NotedXDbContext(DbContextOptions<NotedXDbContext> options)
        : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Page> Pages => Set<Page>();

    public DbSet<Block> Blocks => Set<Block>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<Revision> Revisions => Set<Revision>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<PageTag> PageTags => Set<PageTag>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotedXDbContext).Assembly);
    }
}
