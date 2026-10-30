using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using NotedX.Server.Security;

namespace NotedX.Server.Api;

internal static class PairingEndpoints
{
    public const string RateLimitPolicy = "pairing";

    public static void MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/pair", RedeemAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);
    }

    private static async Task<IResult> RedeemAsync(
        PairRequest request,
        PairingService pairing,
        HttpContext http,
        ILogger<PairingService> logger,
        CancellationToken cancellationToken)
    {
        var result = await pairing.RedeemAsync(
            request.Token,
            request.Pin,
            request.DeviceName,
            cancellationToken);

        http.Response.Headers.CacheControl = "no-store";

        if (!result.Succeeded || result.Device is null || result.DeviceToken is null)
        {
            logger.LogWarning("A pairing attempt was rejected with status {PairingStatus}.", result.Status);

            return result.Status switch
            {
                PairingStatus.PinRequired => Results.Problem(
                    detail: "Enter the four-digit PIN shown on the desktop.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "PIN required"),
                PairingStatus.InvalidPin => Results.Problem(
                    detail: "The PIN is incorrect.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Invalid PIN"),
                _ => Results.Problem(
                    detail: "This pairing code is no longer valid. Scan a fresh QR code on the desktop.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Pairing failed")
            };
        }

        return Results.Ok(new PairResponse(result.Device.Id, result.Device.Name, result.DeviceToken));
    }
}
