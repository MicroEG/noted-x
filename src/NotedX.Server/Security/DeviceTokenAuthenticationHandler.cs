using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using NotedX.Core.Data;
using NotedX.Core.Domain;
using NotedX.Core.Security;

namespace NotedX.Server.Security;

/// <summary>
/// Authenticates requests by hashing the bearer token and looking up the paired device.
/// </summary>
/// <remarks>
/// Only 256-bit random tokens are accepted, so hash lookups are not directly comparable
/// to attacker probes. Raw tokens are never logged. SignalR may pass the token as an
/// <c>access_token</c> query parameter on <c>/hub</c> because browsers cannot set headers
/// on WebSocket handshakes.
/// </remarks>
internal sealed class DeviceTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const int MaxTokenLength = 200;
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LastSeenWrites = new();

    private readonly IDbContextFactory<NotedXDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    public DeviceTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IDbContextFactory<NotedXDbContext> contextFactory,
        TimeProvider timeProvider)
        : base(options, loggerFactory, encoder)
    {
        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (token.Length is < 16 or > MaxTokenLength)
        {
            Logger.LogDebug("Rejected a device token with an invalid length.");
            return AuthenticateResult.Fail("Invalid device token.");
        }

        var cancellationToken = Context.RequestAborted;
        var tokenHash = TokenHasher.Hash(token);

        Device? device;
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            device = await db.Devices
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.TokenHash == tokenHash, cancellationToken);

            if (device is null || device.IsRevoked)
            {
                Logger.LogDebug("Rejected an unknown or revoked device token.");
                return AuthenticateResult.Fail("Invalid device token.");
            }

            await TouchLastSeenAsync(db, device, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return AuthenticateResult.NoResult();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Device authentication failed while reading the device store.");
            return AuthenticateResult.Fail("Device authentication is unavailable.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, device.Id.ToString()),
            new Claim(ClaimTypes.Name, device.Name),
            new Claim(DeviceClaims.DeviceId, device.Id.ToString()),
            new Claim(DeviceClaims.DeviceName, device.Name)
        };

        var identity = new ClaimsIdentity(claims, DeviceClaims.Scheme);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, DeviceClaims.Scheme));
    }

    private string? ReadToken()
    {
        if (Request.Headers.TryGetValue(HeaderNames.Authorization, out var headers) &&
            AuthenticationHeaderValue.TryParse(headers.ToString(), out var header) &&
            string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(header.Parameter))
        {
            return header.Parameter;
        }

        if (Request.Path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase))
        {
            var accessToken = Request.Query["access_token"].ToString();
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                return accessToken;
            }
        }

        return null;
    }

    private async Task TouchLastSeenAsync(
        NotedXDbContext db,
        Device device,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (device.LastSeenAt is { } lastSeen && now - lastSeen < LastSeenInterval)
        {
            return;
        }

        if (LastSeenWrites.TryGetValue(device.Id, out var attempted) &&
            now - attempted < LastSeenInterval)
        {
            return;
        }

        LastSeenWrites[device.Id] = now;

        try
        {
            await db.Devices
                .Where(d => d.Id == device.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(d => d.LastSeenAt, now),
                    cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not record the last-seen time for device {DeviceId}.", device.Id);
        }
    }
}
