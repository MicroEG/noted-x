using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotedX.Core.Domain;

namespace NotedX.Core.Data.Configurations;

/// <summary>Maps <see cref="Device"/> to the Devices table.</summary>
public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("Devices");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name).IsRequired().HasMaxLength(100);
        builder.Property(d => d.TokenHash).IsRequired().HasMaxLength(64);

        builder.HasIndex(d => d.TokenHash).IsUnique();
    }
}
