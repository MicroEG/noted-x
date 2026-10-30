using Microsoft.EntityFrameworkCore;
using NotedX.Core.Data;

namespace NotedX.Server.Api;

internal static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/devices", ListAsync);
        api.MapDelete("/devices/{id:guid}", RevokeAsync);
    }

    private static async Task<IResult> ListAsync(
        IDbContextFactory<NotedXDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.Devices
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return Results.Ok(devices
            .OrderBy(device => device.CreatedAt)
            .Select(device => new DeviceDto(
                device.Id,
                device.Name,
                device.CreatedAt,
                device.LastSeenAt,
                device.IsRevoked))
            .ToArray());
    }

    private static async Task<IResult> RevokeAsync(
        Guid id,
        IDbContextFactory<NotedXDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return ApiResults.NotFound("That device was not found.");
        }

        if (!device.IsRevoked)
        {
            device.IsRevoked = true;
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.NoContent();
    }
}
