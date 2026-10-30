using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotedX.Core.Domain;

namespace NotedX.Core.Data.Configurations;

/// <summary>Maps <see cref="Page"/> to the Pages table and configures its tree indexes.</summary>
public sealed class PageConfiguration : IEntityTypeConfiguration<Page>
{
    public void Configure(EntityTypeBuilder<Page> builder)
    {
        builder.ToTable("Pages");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Title).IsRequired().HasMaxLength(500);
        builder.Property(p => p.Icon).HasMaxLength(64);
        builder.Property(p => p.SearchText).IsRequired();

        builder.HasIndex(p => new { p.WorkspaceId, p.ParentId, p.Position });
        builder.HasIndex(p => new { p.WorkspaceId, p.IsDeleted, p.UpdatedAt });

        builder.HasOne(p => p.Workspace)
            .WithMany(w => w.Pages)
            .HasForeignKey(p => p.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.Parent)
            .WithMany(p => p.Children)
            .HasForeignKey(p => p.ParentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
