using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NotedX.Server.Net;

namespace NotedX.Server.Discovery;

/// <summary>Answers UDP discovery probes so phones can find the server when mDNS is blocked.</summary>
/// <remarks>
/// The responder binds <see cref="IPAddress.Any"/> and only replies to probes from loopback or
/// private IPv4 addresses. Replies are rate-limited per source and contain no tokens or secrets;
/// pairing always happens later over the authenticated HTTP API.
/// </remarks>
public sealed class UdpDiscoveryResponder : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MinimumResponseInterval = TimeSpan.FromSeconds(1);
    private const int MaxTrackedSources = 512;

    private readonly int _requestedPort;
    private readonly Func<DiscoveryAnnouncement> _announcement;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UdpDiscoveryResponder> _logger;
    private readonly ConcurrentDictionary<IPAddress, long> _lastResponses = new();

    private UdpClient? _client;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private int _boundPort;
    private bool _disposed;

    /// <summary>Creates a responder for the configured discovery port.</summary>
    /// <param name="port">UDP port to bind; zero selects an ephemeral port.</param>
    /// <param name="announcement">Builds the reply sent to each accepted probe.</param>
    /// <param name="logger">Logger for lifecycle and rejected-probe events.</param>
    /// <param name="timeProvider">Clock used by the per-source rate limiter; defaults to the system clock.</param>
    public UdpDiscoveryResponder(
        int port,
        Func<DiscoveryAnnouncement> announcement,
        ILogger<UdpDiscoveryResponder> logger,
        TimeProvider? timeProvider = null)
    {
        _requestedPort = port;
        _announcement = announcement ?? throw new ArgumentNullException(nameof(announcement));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Whether the responder is currently listening.</summary>
    public bool IsRunning => _client is not null;

    /// <summary>The port actually bound, or the requested port before startup.</summary>
    public int Port => _client is null ? _requestedPort : _boundPort;

    /// <summary>Whether probes from <paramref name="address"/> are answered.</summary>
    /// <remarks>Only IPv4 loopback and private/CGNAT/link-local addresses are allowed.</remarks>
    public static bool IsAllowedSource(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.AddressFamily == AddressFamily.InterNetwork &&
               (IPAddress.IsLoopback(address) || LanAddressProvider.IsPrivate(address));
    }

    /// <summary>Starts listening on the configured UDP port.</summary>
    /// <exception cref="SocketException">The port could not be bound.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_client is not null)
        {
            return Task.CompletedTask;
        }

        var client = new UdpClient(new IPEndPoint(IPAddress.Any, _requestedPort));
        try
        {
            TryDisableConnectionReset(client.Client);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UDP connection reset suppression is unavailable on this platform");
        }

        _boundPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        _client = client;
        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(client, _cancellation.Token), CancellationToken.None);
        _logger.LogInformation("UDP discovery is listening on port {Port}", _boundPort);

        return Task.CompletedTask;
    }

    /// <summary>Stops listening and disposes the UDP socket.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        var cancellation = _cancellation;
        var loop = _loop;

        _client = null;
        _cancellation = null;
        _loop = null;

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        client?.Dispose();

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _logger.LogDebug(ex, "The UDP discovery loop did not stop within the shutdown window");
            }
        }

        cancellation?.Dispose();
        _lastResponses.Clear();
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

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;

            try
            {
                result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                _logger.LogDebug("Ignored an oversized UDP discovery datagram");
                continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The UDP discovery listener failed; retrying");

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                await HandleProbeAsync(client, result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignored a UDP discovery probe that could not be answered");
            }
        }
    }

    private async Task HandleProbeAsync(
        UdpClient client,
        UdpReceiveResult result,
        CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(result.Buffer).Trim();
        if (!string.Equals(text, DiscoveryAnnouncement.Probe, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var source = result.RemoteEndPoint.Address;
        if (!IsAllowedSource(source))
        {
            _logger.LogDebug("Ignored a discovery probe from non-local address {Address}", source);
            return;
        }

        if (!TryReserveResponse(source))
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(_announcement(), JsonOptions);
        await client.SendAsync(payload, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Answered a discovery probe from {Address}", source);
    }

    private bool TryReserveResponse(IPAddress source)
    {
        var now = _timeProvider.GetTimestamp();

        if (_lastResponses.TryGetValue(source, out var last) &&
            _timeProvider.GetElapsedTime(last, now) < MinimumResponseInterval)
        {
            return false;
        }

        _lastResponses[source] = now;
        if (_lastResponses.Count > MaxTrackedSources)
        {
            foreach (var entry in _lastResponses)
            {
                if (_timeProvider.GetElapsedTime(entry.Value, now) >= MinimumResponseInterval)
                {
                    _lastResponses.TryRemove(entry.Key, out _);
                }
            }
        }

        return true;
    }

    private static void TryDisableConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int SioUdpConnReset = unchecked((int)0x9800000C);
        socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
    }
}
