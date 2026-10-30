using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotedX.Core.Domain;

namespace NotedX.Core.Data.Configurations;

/// <summary>Maps the page/tag join table.</summary>
public sealed class PageTagConfiguration : IEntityTypeConfiguration<PageTag>
{
    public void Configure(EntityTypeBuilder<PageTag> builder)
    {
        builder.ToTable("PageTags");
        builder.HasKey(pt => new { pt.PageId, pt.TagId });

        builder.HasOne(pt => pt.Page)
            .WithMany(p => p.PageTags)
            .HasForeignKey(pt => pt.PageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(pt => pt.Tag)
            .WithMany(t => t.PageTags)
            .HasForeignKey(pt => pt.TagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
