namespace NotedX.Server.Discovery;

/// <summary>Secret-free description of a running server shared with local network clients.</summary>
/// <param name="Protocol">Discovery protocol identifier, currently <see cref="CurrentProtocol"/>.</param>
/// <param name="Service">Friendly name of the application instance.</param>
/// <param name="Machine">Name of the computer hosting the server.</param>
/// <param name="Port">TCP port of the local web server.</param>
/// <param name="Https">Whether the web server uses HTTPS.</param>
/// <param name="BaseUrl">Base URL a phone can open; never contains a pairing token.</param>
/// <param name="Version">Version of the running application.</param>
public sealed record DiscoveryAnnouncement(
    string Protocol,
    string Service,
    string Machine,
    int Port,
    bool Https,
    string BaseUrl,
    string Version)
{
    /// <summary>Identifier included in every announcement.</summary>
    public const string CurrentProtocol = "noted-x/1";

    /// <summary>Payload a client broadcasts to locate a server without mDNS.</summary>
    public const string Probe = "NOTEDX/1 DISCOVER";

    /// <summary>Largest probe datagram accepted by the UDP responder.</summary>
    public const int MaxProbeBytes = 512;
}
