namespace NotedX.Server;

/// <summary>Settings that control the embedded local network server.</summary>
public sealed class ServerSettings
{
    /// <summary>Default TCP port tried before falling back to the next free port.</summary>
    public const int DefaultPort = 5177;

    /// <summary>mDNS service type advertised on the local network.</summary>
    public const string MdnsServiceType = "_noted-x._tcp";

    /// <summary>Size of each resumable upload chunk (1 MB).</summary>
    public const int DefaultChunkSizeBytes = 1024 * 1024;

    /// <summary>Default UDP port used for LAN discovery probes.</summary>
    public const int DefaultDiscoveryPort = 50000;

    /// <summary>Preferred listening port.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Interface Kestrel binds to. Defaults to <c>0.0.0.0</c> (all LAN interfaces);
    /// tests use <c>127.0.0.1</c>.
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>How many consecutive ports to try when the preferred port is busy.</summary>
    public int PortFallbackAttempts { get; set; } = 20;

    /// <summary>Friendly name advertised over mDNS and shown to mobile devices.</summary>
    public string ServiceName { get; set; } = "noted-x";

    /// <summary>Maximum accepted upload size in bytes (2 GB by default).</summary>
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Size of each resumable upload chunk in bytes.</summary>
    public int ChunkSizeBytes { get; set; } = DefaultChunkSizeBytes;

    /// <summary>How long a pairing token remains valid.</summary>
    public TimeSpan PairingTokenLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum pairing requests accepted per minute from a single address.</summary>
    public int PairingAttemptsPerMinute { get; set; } = 5;

    /// <summary>Whether pairing additionally requires the four-digit PIN shown on the desktop.</summary>
    public bool RequirePairingPin { get; set; } = true;

    /// <summary>Whether the server listens over HTTPS using a self-signed certificate.</summary>
    public bool UseHttps { get; set; }

    /// <summary>UDP port that answers local discovery probes. Zero selects an ephemeral port.</summary>
    public int DiscoveryPort { get; set; } = DefaultDiscoveryPort;

    /// <summary>Whether the server is advertised over multicast DNS.</summary>
    public bool EnableMdns { get; set; } = true;

    /// <summary>Whether the server answers UDP broadcast discovery probes.</summary>
    public bool EnableUdpDiscovery { get; set; } = true;
}
