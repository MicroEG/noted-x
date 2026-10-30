using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NotedX.Core.Storage;
using NotedX.Server;
using NotedX.Server.Api;
using NotedX.Server.Net;

namespace NotedX.Tests;

/// <summary>Runs a real Kestrel server on loopback over a temporary database and blob store.</summary>
internal sealed class ServerTestHarness : IAsyncDisposable
{
    /// <summary>JSON options that match the server's camelCase contract.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root;

    private ServerTestHarness(
        string root,
        AppPaths paths,
        ServerSettings settings,
        NotedXWebServer server,
        HttpClient client,
        ManualTimeProvider clock)
    {
        _root = root;
        Paths = paths;
        Settings = settings;
        Server = server;
        Client = client;
        Clock = clock;
    }

    public AppPaths Paths { get; }

    public ServerSettings Settings { get; }

    public NotedXWebServer Server { get; }

    public HttpClient Client { get; }

    public ManualTimeProvider Clock { get; }

    /// <summary>Starts an isolated server and an HTTP client pointed at its loopback port.</summary>
    public static async Task<ServerTestHarness> StartAsync(Action<ServerSettings>? configure = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "notedx-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        var settings = new ServerSettings
        {
            BindAddress = "127.0.0.1",
            Port = FindFreePort(),
            PortFallbackAttempts = 5,
            RequirePairingPin = true,
            PairingAttemptsPerMinute = 500,
            ChunkSizeBytes = 256 * 1024
        };

        configure?.Invoke(settings);

        var paths = new AppPaths(root);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var server = new NotedXWebServer(
            settings,
            paths,
            NullLoggerFactory.Instance,
            NullLogger<NotedXWebServer>.Instance,
            clock);

        await server.StartAsync(CancellationToken.None);

        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        return new ServerTestHarness(root, paths, settings, server, client, clock);
    }

    /// <summary>Creates a pairing grant, redeems it, and returns the issued device token.</summary>
    public async Task<PairResponse> PairAsync(string? pin = null, string deviceName = "Test phone")
    {
        var session = Server.Pairing.CreateSession();
        var response = await Client.PostAsJsonAsync(
            "/pair",
            new PairRequest(session.Token, pin ?? session.Pin, deviceName),
            Json);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<PairResponse>(Json);
        Assert.NotNull(payload);
        return payload!;
    }

    /// <summary>Sends the bearer token on every subsequent request.</summary>
    public void Authorize(string token)
    {
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        try
        {
            await Server.DisposeAsync();
        }
        catch (Exception)
        {
            // Disposal problems must not mask test failures.
        }

        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a locked test directory is harmless.
        }
    }

    private static int FindFreePort()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Random.Shared.Next(52000, 60000);
            if (PortUtility.IsFree(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No free loopback port was found for the test server.");
    }
}

/// <summary>Clock that tests can move forward deterministically.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}
