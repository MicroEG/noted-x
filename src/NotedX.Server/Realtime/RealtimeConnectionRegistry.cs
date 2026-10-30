using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotedX.Core.Data;

namespace NotedX.Server.Realtime;

/// <summary>Closes SignalR connections whose device is revoked after the socket was opened.</summary>
public sealed class RealtimeConnectionRegistry
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<NotedXDbContext> _contextFactory;
    private readonly ILogger<RealtimeConnectionRegistry> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _connections = new();

    /// <summary>Creates the registry.</summary>
    public RealtimeConnectionRegistry(
        IDbContextFactory<NotedXDbContext> contextFactory,
        ILogger<RealtimeConnectionRegistry> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>Starts watching a connection and aborts it when its device is revoked.</summary>
    public void Track(string connectionId, Guid deviceId, Action abort)
    {
        var cancellation = new CancellationTokenSource();
        _connections[connectionId] = cancellation;
        _ = MonitorAsync(connectionId, deviceId, abort, cancellation);
    }

    /// <summary>Stops watching a connection that has closed.</summary>
    public void Release(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    private async Task MonitorAsync(
        string connectionId,
        Guid deviceId,
        Action abort,
        CancellationTokenSource cancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(CheckInterval);

            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                try
                {
                    await using var db = await _contextFactory.CreateDbContextAsync(cancellation.Token);
                    var isActive = await db.Devices
                        .AsNoTracking()
                        .AnyAsync(device => device.Id == deviceId && !device.IsRevoked, cancellation.Token);

                    if (!isActive)
                    {
                        _logger.LogInformation(
                            "Closing the realtime channel of revoked device {DeviceId}.",
                            deviceId);
                        abort();
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "The realtime revocation check failed; it will retry.");
                }
            }
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            cancellation.Dispose();
        }
    }
}
