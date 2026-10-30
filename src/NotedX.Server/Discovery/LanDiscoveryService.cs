using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NotedX.Server.Discovery;

/// <summary>Publishes the running web server over mDNS and a UDP probe fallback.</summary>
/// <remarks>
/// Register this hosted service after <see cref="NotedXWebServer"/> so the advertised port,
/// URL, and scheme are final. Discovery failures are logged but never block application startup.
/// </remarks>
public sealed class LanDiscoveryService : IHostedService, IAsyncDisposable
{
    private readonly NotedXWebServer _server;
    private readonly ServerSettings _settings;
    private readonly ILogger<LanDiscoveryService> _logger;
    private readonly ILogger<UdpDiscoveryResponder> _udpLogger;
    private readonly ILogger<MdnsAdvertiser> _mdnsLogger;
    private readonly TimeProvider _timeProvider;

    private UdpDiscoveryResponder? _udpResponder;
    private MdnsAdvertiser? _mdnsAdvertiser;
    private bool _disposed;

    /// <summary>Creates the discovery service for the embedded web server.</summary>
    /// <param name="server">The web server whose address is announced.</param>
    /// <param name="settings">Discovery ports and feature switches.</param>
    /// <param name="logger">Logger for aggregate discovery state.</param>
    /// <param name="udpLogger">Logger for the UDP responder.</param>
    /// <param name="mdnsLogger">Logger for the mDNS advertiser.</param>
    /// <param name="timeProvider">Clock used by the UDP responder's rate limiter.</param>
    public LanDiscoveryService(
        NotedXWebServer server,
        ServerSettings settings,
        ILogger<LanDiscoveryService> logger,
        ILogger<UdpDiscoveryResponder> udpLogger,
        ILogger<MdnsAdvertiser> mdnsLogger,
        TimeProvider timeProvider)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _udpLogger = udpLogger ?? throw new ArgumentNullException(nameof(udpLogger));
        _mdnsLogger = mdnsLogger ?? throw new ArgumentNullException(nameof(mdnsLogger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>The public description answered to probes, once discovery has started.</summary>
    public DiscoveryAnnouncement? Announcement { get; private set; }

    /// <summary>Whether the UDP probe responder is listening.</summary>
    public bool UdpRunning => _udpResponder?.IsRunning == true;

    /// <summary>Whether the mDNS advertisement is active.</summary>
    public bool MdnsRunning => _mdnsAdvertiser?.IsRunning == true;

    /// <summary>The active UDP discovery port, or <c>null</c> when the responder is stopped.</summary>
    public int? UdpPort => UdpRunning ? _udpResponder!.Port : null;

    /// <summary>Starts the enabled discovery transports after the web server is listening.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_server.IsRunning || _server.BaseUrl is null)
        {
            _logger.LogWarning("LAN discovery was skipped because the web server is not running");
            return;
        }

        Announcement = new DiscoveryAnnouncement(
            DiscoveryAnnouncement.CurrentProtocol,
            _settings.ServiceName,
            Environment.MachineName,
            _server.Port,
            _server.UsesHttps,
            _server.BaseUrl,
            GetVersion());

        if (_settings.EnableMdns)
        {
            var advertiser = new MdnsAdvertiser(_settings, GetAnnouncement, _mdnsLogger);
            try
            {
                await advertiser.StartAsync(cancellationToken).ConfigureAwait(false);
                _mdnsAdvertiser = advertiser;
            }
            catch (OperationCanceledException)
            {
                await advertiser.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mDNS advertising could not be started");
                await advertiser.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (_settings.EnableUdpDiscovery)
        {
            var responder = new UdpDiscoveryResponder(
                _settings.DiscoveryPort,
                GetAnnouncement,
                _udpLogger,
                _timeProvider);
            try
            {
                await responder.StartAsync(cancellationToken).ConfigureAwait(false);
                _udpResponder = responder;
            }
            catch (OperationCanceledException)
            {
                await responder.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UDP discovery could not be started");
                await responder.DisposeAsync().ConfigureAwait(false);
            }
        }

        _logger.LogInformation(
            "LAN discovery started (mDNS: {Mdns}, UDP: {Udp}, port: {Port})",
            MdnsRunning ? "yes" : "no",
            UdpRunning ? _udpResponder!.Port.ToString() : "no",
            _server.Port);
    }

    /// <summary>Stops both discovery transports, ignoring individual failures.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var udp = _udpResponder;
        var mdns = _mdnsAdvertiser;
        _udpResponder = null;
        _mdnsAdvertiser = null;

        if (udp is not null)
        {
            try
            {
                await udp.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The UDP discovery responder failed to stop cleanly");
            }

            await udp.DisposeAsync().ConfigureAwait(false);
        }

        if (mdns is not null)
        {
            try
            {
                await mdns.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The mDNS advertiser failed to stop cleanly");
            }

            await mdns.DisposeAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("LAN discovery stopped");
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

    private DiscoveryAnnouncement GetAnnouncement() =>
        Announcement ?? throw new InvalidOperationException("LAN discovery has not started.");

    private static string GetVersion() =>
        typeof(LanDiscoveryService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
