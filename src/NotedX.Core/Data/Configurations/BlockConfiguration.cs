using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotedX.Core.Domain;

namespace NotedX.Core.Data.Configurations;

/// <summary>Maps <see cref="Block"/> to the Blocks table.</summary>
public sealed class BlockConfiguration : IEntityTypeConfiguration<Block>
{
    public void Configure(EntityTypeBuilder<Block> builder)
    {
        builder.ToTable("Blocks");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(b => b.Content).IsRequired();

        builder.HasIndex(b => new { b.PageId, b.Position });

        builder.HasOne(b => b.Page)
            .WithMany(p => p.Blocks)
            .HasForeignKey(b => b.PageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
