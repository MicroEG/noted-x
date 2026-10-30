using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotedX.Core.Domain;

namespace NotedX.Core.Data.Configurations;

/// <summary>Maps <see cref="Tag"/> to the Tags table.</summary>
public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(64).UseCollation("NOCASE");
        builder.Property(t => t.Color).HasMaxLength(16);

        builder.HasIndex(t => new { t.WorkspaceId, t.Name }).IsUnique();

        builder.HasOne(t => t.Workspace)
            .WithMany(w => w.Tags)
            .HasForeignKey(t => t.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
