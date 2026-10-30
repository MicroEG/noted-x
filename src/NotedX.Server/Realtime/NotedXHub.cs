using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NotedX.Server.Security;

namespace NotedX.Server.Realtime;

/// <summary>SignalR hub that pushes page changes and transfer progress to paired devices.</summary>
[Authorize]
public sealed class NotedXHub : Hub
{
    private readonly RealtimeConnectionRegistry _connections;
    private readonly ILogger<NotedXHub> _logger;

    /// <summary>Creates the hub.</summary>
    public NotedXHub(RealtimeConnectionRegistry connections, ILogger<NotedXHub> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        var deviceName = Context.User?.FindFirst(DeviceClaims.DeviceName)?.Value ?? "unknown device";
        _logger.LogInformation("Realtime channel opened for {DeviceName}.", deviceName);

        var deviceIdValue = Context.User?.FindFirst(DeviceClaims.DeviceId)?.Value;
        if (Guid.TryParse(deviceIdValue, out var deviceId))
        {
            _connections.Track(Context.ConnectionId, deviceId, Context.Abort);
        }

        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Release(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Returns the server time so a client can verify the connection.</summary>
    public string Ping() => DateTimeOffset.UtcNow.ToString("O");
}
