namespace NotedX.Core.Domain;

/// <summary>A paired mobile device. Only a hash of its bearer token is persisted.</summary>
public sealed class Device
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-case hexadecimal SHA-256 hash of the device token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public bool IsRevoked { get; set; }
}
