using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Makaretu.Dns;
using Microsoft.Extensions.Logging.Abstractions;
using NotedX.Server;
using NotedX.Server.Discovery;

namespace NotedX.Tests;

public sealed class LanDiscoveryTests
{
    private static readonly JsonSerializerOptions Json = ServerTestHarness.Json;

    [Fact]
    public async Task Udp_discovery_answers_probes_without_leaking_secrets()
    {
        var announcement = CreateAnnouncement();
        await using var responder = new UdpDiscoveryResponder(
            0,
            () => announcement,
            NullLogger<UdpDiscoveryResponder>.Instance);

        await responder.StartAsync(CancellationToken.None);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(
            Encoding.UTF8.GetBytes(DiscoveryAnnouncement.Probe),
            new IPEndPoint(IPAddress.Loopback, responder.Port));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.ReceiveAsync(timeout.Token);
        var raw = Encoding.UTF8.GetString(response.Buffer);
        var payload = JsonSerializer.Deserialize<DiscoveryAnnouncement>(response.Buffer, Json);

        Assert.NotNull(payload);
        Assert.Equal(DiscoveryAnnouncement.CurrentProtocol, payload!.Protocol);
        Assert.Equal("noted-x", payload.Service);
        Assert.Equal("TEST-PC", payload.Machine);
        Assert.Equal(5177, payload.Port);
        Assert.False(payload.Https);
        Assert.Equal("http://192.168.1.20:5177", payload.BaseUrl);
        Assert.Equal("0.1.0", payload.Version);

        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pin", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Udp_discovery_ignores_unrelated_datagrams_and_stays_available()
    {
        await using var responder = new UdpDiscoveryResponder(
            0,
            CreateAnnouncement,
            NullLogger<UdpDiscoveryResponder>.Instance);

        await responder.StartAsync(CancellationToken.None);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var server = new IPEndPoint(IPAddress.Loopback, responder.Port);

        await client.SendAsync(Encoding.UTF8.GetBytes("hello there"), server);

        using (var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await client.ReceiveAsync(quiet.Token));
        }

        await client.SendAsync(Encoding.UTF8.GetBytes(DiscoveryAnnouncement.Probe), server);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.ReceiveAsync(timeout.Token);

        Assert.NotEmpty(response.Buffer);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.4.9", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("100.64.0.10", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("2001:db8::1", false)]
    public void Discovery_only_answers_loopback_and_private_sources(string address, bool allowed)
    {
        Assert.Equal(allowed, UdpDiscoveryResponder.IsAllowedSource(IPAddress.Parse(address)));
    }

    [Fact]
    public void Mdns_profile_describes_the_service_without_secrets()
    {
        var settings = new ServerSettings { ServiceName = "noted-x" };
        var announcement = CreateAnnouncement();

        var profile = MdnsAdvertiser.CreateProfile(
            settings,
            announcement,
            [IPAddress.Parse("192.168.1.20")]);

        Assert.Contains("_noted-x._tcp", profile.QualifiedServiceName.ToString(), StringComparison.Ordinal);

        var service = Assert.Single(profile.Resources.OfType<SRVRecord>());
        Assert.Equal((ushort)5177, service.Port);

        var properties = profile.Resources
            .OfType<TXTRecord>()
            .SelectMany(record => record.Strings)
            .ToArray();

        Assert.Contains("protocol=noted-x/1", properties);
        Assert.Contains("version=0.1.0", properties);
        Assert.Contains("https=0", properties);
        Assert.Contains("machine=TEST-PC", properties);
        Assert.DoesNotContain(properties, entry => entry.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, entry => entry.Contains("pin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Lan_discovery_service_announces_the_running_server()
    {
        await using var harness = await ServerTestHarness.StartAsync(settings =>
        {
            settings.EnableMdns = false;
            settings.DiscoveryPort = 0;
        });

        await using var discovery = new LanDiscoveryService(
            harness.Server,
            harness.Settings,
            NullLogger<LanDiscoveryService>.Instance,
            NullLogger<UdpDiscoveryResponder>.Instance,
            NullLogger<MdnsAdvertiser>.Instance,
            TimeProvider.System);

        await discovery.StartAsync(CancellationToken.None);

        Assert.True(discovery.UdpRunning);
        Assert.False(discovery.MdnsRunning);
        Assert.NotNull(discovery.Announcement);
        Assert.NotNull(discovery.UdpPort);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(
            Encoding.UTF8.GetBytes(DiscoveryAnnouncement.Probe),
            new IPEndPoint(IPAddress.Loopback, discovery.UdpPort.Value));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.ReceiveAsync(timeout.Token);
        var payload = JsonSerializer.Deserialize<DiscoveryAnnouncement>(response.Buffer, Json);

        Assert.NotNull(payload);
        Assert.Equal(harness.Server.Port, payload!.Port);
        Assert.Equal(harness.Server.BaseUrl, payload.BaseUrl);
        Assert.Equal(harness.Settings.ServiceName, payload.Service);
        Assert.Equal(Environment.MachineName, payload.Machine);
    }

    private static DiscoveryAnnouncement CreateAnnouncement() => new(
        DiscoveryAnnouncement.CurrentProtocol,
        "noted-x",
        "TEST-PC",
        5177,
        Https: false,
        "http://192.168.1.20:5177",
        "0.1.0");
}
