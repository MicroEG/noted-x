using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotedX.Core.Data;

namespace NotedX.Server.Security;

/// <summary>Metadata about a paired device. Token hashes are never exposed.</summary>
/// <param name="Id">Device identifier.</param>
/// <param name="Name">Friendly name supplied during pairing.</param>
/// <param name="CreatedAt">When the device was paired.</param>
/// <param name="LastSeenAt">Last authenticated request, when known.</param>
/// <param name="IsRevoked">Whether the device token has been rejected.</param>
public sealed record PairedDevice(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    bool IsRevoked);

/// <summary>Lists and revokes paired mobile devices.</summary>
public sealed class DeviceService
{
    private readonly IDbContextFactory<NotedXDbContext> _contextFactory;
    private readonly ILogger<DeviceService> _logger;

    /// <summary>Creates the service over the shared database.</summary>
    public DeviceService(
        IDbContextFactory<NotedXDbContext> contextFactory,
        ILogger<DeviceService> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Lists paired devices, oldest first.</summary>
    public async Task<IReadOnlyList<PairedDevice>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.Devices.AsNoTracking().ToListAsync(cancellationToken);

        return devices
            .OrderBy(device => device.CreatedAt)
            .Select(device => new PairedDevice(
                device.Id,
                device.Name,
                device.CreatedAt,
                device.LastSeenAt,
                device.IsRevoked))
            .ToArray();
    }

    /// <summary>Revokes a device token. Connected clients are disconnected by the hub watchdog.</summary>
    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return false;
        }

        if (!device.IsRevoked)
        {
            device.IsRevoked = true;
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Revoked paired device {DeviceId}.", device.Id);
        }

        return true;
    }
}
