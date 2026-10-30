using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotedX.Core.Domain;

namespace NotedX.Core.Data.Configurations;

/// <summary>Maps <see cref="Attachment"/> metadata to the Attachments table.</summary>
public sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("Attachments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.BlobHash).IsRequired().HasMaxLength(64);
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(512);
        builder.Property(a => a.MimeType).IsRequired().HasMaxLength(255);
        builder.Property(a => a.UploadedBy).IsRequired().HasMaxLength(128);

        builder.HasIndex(a => a.BlobHash);
        builder.HasIndex(a => new { a.PageId, a.CreatedAt });

        builder.HasOne(a => a.Page)
            .WithMany(p => p.Attachments)
            .HasForeignKey(a => a.PageId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
