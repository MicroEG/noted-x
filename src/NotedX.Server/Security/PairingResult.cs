using NotedX.Core.Domain;

namespace NotedX.Server.Security;

/// <summary>Outcome of a pairing attempt.</summary>
public enum PairingStatus
{
    /// <summary>A device token was issued.</summary>
    Success,

    /// <summary>The token is unknown, already used, or the session expired.</summary>
    InvalidToken,

    /// <summary>The session requires a PIN that was not supplied.</summary>
    PinRequired,

    /// <summary>The supplied PIN is wrong.</summary>
    InvalidPin
}

/// <summary>Result of redeeming a pairing token.</summary>
/// <param name="Status">Why the attempt succeeded or failed.</param>
/// <param name="DeviceToken">Raw bearer token returned once on success. Never persisted.</param>
/// <param name="Device">The newly paired device, or <c>null</c> on failure.</param>
public sealed record PairingResult(PairingStatus Status, string? DeviceToken = null, Device? Device = null)
{
    /// <summary>Whether pairing succeeded.</summary>
    public bool Succeeded => Status == PairingStatus.Success;
}

/// <summary>Raised after a mobile device has been paired.</summary>
public sealed class DevicePairedEventArgs : EventArgs
{
    /// <summary>Creates event arguments for a newly paired device.</summary>
    public DevicePairedEventArgs(Device device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <summary>The paired device. Its token hash is persisted; the raw token is not available here.</summary>
    public Device Device { get; }
}
