using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotedX.Core.Domain;

namespace NotedX.Core.Data.Configurations;

/// <summary>Maps <see cref="Revision"/> snapshots to the Revisions table.</summary>
public sealed class RevisionConfiguration : IEntityTypeConfiguration<Revision>
{
    public void Configure(EntityTypeBuilder<Revision> builder)
    {
        builder.ToTable("Revisions");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ContentSnapshot).IsRequired();

        builder.HasIndex(r => new { r.PageId, r.CreatedAt });

        builder.HasOne(r => r.Page)
            .WithMany(p => p.Revisions)
            .HasForeignKey(r => r.PageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
