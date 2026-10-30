namespace NotedX.Server.Security;

/// <summary>Claim names and the authentication scheme used for paired devices.</summary>
public static class DeviceClaims
{
    /// <summary>Authentication scheme that validates opaque device bearer tokens.</summary>
    public const string Scheme = "NotedXDevice";

    /// <summary>Claim carrying the device identifier.</summary>
    public const string DeviceId = "notedx:device_id";

    /// <summary>Claim carrying the friendly device name.</summary>
    public const string DeviceName = "notedx:device_name";
}
