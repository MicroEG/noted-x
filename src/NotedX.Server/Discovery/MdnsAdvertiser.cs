using System.Net;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using NotedX.Server.Net;

namespace NotedX.Server.Discovery;

/// <summary>Advertises the local server as a <c>_noted-x._tcp</c> mDNS service.</summary>
/// <remarks>Advertising is best effort: failures are logged and never stop the application.</remarks>
public sealed class MdnsAdvertiser : IAsyncDisposable
{
    private readonly ServerSettings _settings;
    private readonly Func<DiscoveryAnnouncement> _announcement;
    private readonly ILogger<MdnsAdvertiser> _logger;

    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;
    private bool _disposed;

    /// <summary>Creates an advertiser over the given server settings.</summary>
    /// <param name="settings">Service name and discovery preferences.</param>
    /// <param name="announcement">Builds the public description of the running server.</param>
    /// <param name="logger">Logger for advertisement lifecycle events.</param>
    public MdnsAdvertiser(
        ServerSettings settings,
        Func<DiscoveryAnnouncement> announcement,
        ILogger<MdnsAdvertiser> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _announcement = announcement ?? throw new ArgumentNullException(nameof(announcement));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether the service is currently advertised.</summary>
    public bool IsRunning => _profile is not null;

    /// <summary>The fully qualified instance name while advertising.</summary>
    public string? AdvertisedName => _profile?.FullyQualifiedName.ToString();

    /// <summary>Builds the DNS-SD profile for the running server without touching the network.</summary>
    /// <param name="settings">Service name settings.</param>
    /// <param name="announcement">Public description of the running server.</param>
    /// <param name="addresses">IPv4 addresses the service is reachable on.</param>
    public static ServiceProfile CreateProfile(
        ServerSettings settings,
        DiscoveryAnnouncement announcement,
        IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(addresses);

        var profile = new ServiceProfile(
            settings.ServiceName,
            ServerSettings.MdnsServiceType,
            (ushort)announcement.Port,
            addresses);

        profile.AddProperty("protocol", announcement.Protocol);
        profile.AddProperty("version", announcement.Version);
        profile.AddProperty("https", announcement.Https ? "1" : "0");
        profile.AddProperty("machine", announcement.Machine);

        return profile;
    }

    /// <summary>Starts advertising on every private IPv4 interface.</summary>
    /// <remarks>When the machine has no private address the advertiser stays stopped.</remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        var addresses = LanAddressProvider.GetAddresses()
            .Select(address => IPAddress.Parse(address.Address))
            .ToArray();

        if (addresses.Length == 0)
        {
            _logger.LogWarning(
                "mDNS advertising was skipped because no private network address is available");
            return Task.CompletedTask;
        }

        var announcement = _announcement();
        var profile = CreateProfile(_settings, announcement, addresses);
        var mdns = new MulticastService();
        var discovery = new ServiceDiscovery(mdns);

        try
        {
            mdns.Start();
            discovery.Advertise(profile);
        }
        catch (Exception)
        {
            discovery.Dispose();
            mdns.Dispose();
            throw;
        }

        _mdns = mdns;
        _discovery = discovery;
        _profile = profile;

        _logger.LogInformation(
            "Advertising {ServiceName} over mDNS as {InstanceName}",
            _settings.ServiceName,
            profile.FullyQualifiedName);

        return Task.CompletedTask;
    }

    /// <summary>Sends a goodbye message and stops advertising.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        var discovery = _discovery;
        var mdns = _mdns;
        var profile = _profile;

        _discovery = null;
        _mdns = null;
        _profile = null;

        if (discovery is not null)
        {
            try
            {
                if (profile is not null)
                {
                    discovery.Unadvertise(profile);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The mDNS goodbye message could not be sent");
            }

            try
            {
                discovery.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The mDNS service discovery failed to dispose");
            }
        }

        if (mdns is not null)
        {
            try
            {
                mdns.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The mDNS listener failed to stop");
            }

            try
            {
                mdns.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The mDNS listener failed to dispose");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
