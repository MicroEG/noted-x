using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotedX.Core.Data;
using NotedX.Core.Domain;
using NotedX.Core.Security;

namespace NotedX.Server.Security;

/// <summary>Issues short-lived pairing grants and exchanges them for device tokens.</summary>
/// <remarks>
/// Only one grant is active at a time. Tokens are single-use, expire after
/// <see cref="ServerSettings.PairingTokenLifetime"/>, and are compared in constant time.
/// Raw tokens and PINs are never logged or persisted.
/// </remarks>
public sealed class PairingService
{
    private readonly IDbContextFactory<NotedXDbContext> _contextFactory;
    private readonly ServerSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PairingService> _logger;
    private readonly object _gate = new();

    private byte[]? _tokenHash;
    private byte[]? _pinHash;
    private DateTimeOffset _expiresAt;
    private bool _active;
    private bool _redeemed;

    /// <summary>Creates the service.</summary>
    public PairingService(
        IDbContextFactory<NotedXDbContext> contextFactory,
        ServerSettings settings,
        TimeProvider timeProvider,
        ILogger<PairingService> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised after a device is successfully paired.</summary>
    public event EventHandler<DevicePairedEventArgs>? DevicePaired;

    /// <summary>Creates a fresh grant, invalidating any previous one.</summary>
    public PairingSession CreateSession()
    {
        var lifetime = _settings.PairingTokenLifetime;
        if (lifetime < TimeSpan.FromSeconds(30))
        {
            lifetime = TimeSpan.FromSeconds(30);
        }
        else if (lifetime > TimeSpan.FromMinutes(15))
        {
            lifetime = TimeSpan.FromMinutes(15);
        }

        var token = TokenHasher.CreateToken(TokenHasher.PairingTokenBytes);
        var pin = _settings.RequirePairingPin ? TokenHasher.CreatePin() : null;
        var expiresAt = _timeProvider.GetUtcNow().Add(lifetime);

        lock (_gate)
        {
            _tokenHash = TokenHasher.HashBytes(token);
            _pinHash = pin is null ? null : TokenHasher.HashBytes(pin);
            _expiresAt = expiresAt;
            _active = true;
            _redeemed = false;
        }

        _logger.LogInformation("A pairing grant was created and expires at {ExpiresAt:u}.", expiresAt.UtcDateTime);
        return new PairingSession(token, pin, expiresAt);
    }

    /// <summary>Redeems a pairing token (and optional PIN) for a long-lived device token.</summary>
    /// <param name="token">Token taken from the QR URL.</param>
    /// <param name="pin">PIN typed by the user, when required.</param>
    /// <param name="deviceName">Friendly device name supplied by the mobile browser.</param>
    /// <param name="cancellationToken">Cancels the database write.</param>
    public async Task<PairingResult> RedeemAsync(
        string? token,
        string? pin,
        string? deviceName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new PairingResult(PairingStatus.InvalidToken);
        }

        var tokenBytes = Encoding.UTF8.GetBytes(token.Trim());
        var pinBytes = pin is null ? null : Encoding.UTF8.GetBytes(pin.Trim());

        lock (_gate)
        {
            if (!_active || _redeemed || _tokenHash is null)
            {
                return new PairingResult(PairingStatus.InvalidToken);
            }

            if (_timeProvider.GetUtcNow() >= _expiresAt)
            {
                _active = false;
                return new PairingResult(PairingStatus.InvalidToken);
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(tokenBytes),
                    _tokenHash))
            {
                return new PairingResult(PairingStatus.InvalidToken);
            }

            if (_pinHash is not null)
            {
                if (pinBytes is null || pinBytes.Length == 0)
                {
                    return new PairingResult(PairingStatus.PinRequired);
                }

                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(pinBytes), _pinHash))
                {
                    return new PairingResult(PairingStatus.InvalidPin);
                }
            }

            _redeemed = true;
        }

        var deviceToken = TokenHasher.CreateToken(TokenHasher.DeviceTokenBytes);
        var now = _timeProvider.GetUtcNow();
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = NormalizeDeviceName(deviceName),
            TokenHash = TokenHasher.Hash(deviceToken),
            CreatedAt = now,
            LastSeenAt = now,
            IsRevoked = false
        };

        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            db.Devices.Add(device);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            lock (_gate)
            {
                _redeemed = false;
            }

            throw;
        }

        _logger.LogInformation("Paired device {DeviceId}.", device.Id);
        DevicePaired?.Invoke(this, new DevicePairedEventArgs(device));
        return new PairingResult(PairingStatus.Success, deviceToken, device);
    }

    private static string NormalizeDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Mobile device";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim())
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }

            if (builder.Length >= 100)
            {
                break;
            }
        }

        return builder.Length == 0 ? "Mobile device" : builder.ToString();
    }
}
