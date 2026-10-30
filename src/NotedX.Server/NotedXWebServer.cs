using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotedX.Core.Data;
using NotedX.Core.Notes;
using NotedX.Core.Storage;
using NotedX.MobileWeb;
using NotedX.Server.Api;
using NotedX.Server.Https;
using NotedX.Server.Logging;
using NotedX.Server.Net;
using NotedX.Server.Realtime;
using NotedX.Server.Security;
using NotedX.Server.Transfers;

namespace NotedX.Server;

/// <summary>Hosts the embedded Kestrel server that serves the LAN API and the mobile PWA.</summary>
/// <remarks>
/// The server lives inside the desktop process. It binds all interfaces on the first free
/// port at or after <see cref="ServerSettings.Port"/>, requires a paired device token for
/// every API call, and shares the desktop database, blob store, and realtime notifier.
/// </remarks>
public sealed class NotedXWebServer : IHostedService, IAsyncDisposable
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; connect-src 'self' ws: wss:; font-src 'self' data:; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    private static readonly string[] AllHttpMethods =
    [
        HttpMethods.Get,
        HttpMethods.Head,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
        HttpMethods.Options
    ];

    private readonly ServerSettings _settings;
    private readonly AppPaths _paths;
    private readonly ILoggerFactory _hostLoggerFactory;
    private readonly ILogger<NotedXWebServer> _logger;
    private readonly TimeProvider _timeProvider;

    private WebApplication? _app;
    private X509Certificate2? _certificate;
    private string? _baseUrl;
    private int _port;
    private bool _disposed;

    /// <summary>Creates the server over the desktop application's shared services.</summary>
    /// <param name="settings">Network, transfer, and pairing limits.</param>
    /// <param name="paths">Application data locations shared with the desktop.</param>
    /// <param name="hostLoggerFactory">Logger factory that receives the web server's entries.</param>
    /// <param name="logger">Logger for host lifecycle events.</param>
    /// <param name="timeProvider">Clock used for pairing and transfer timestamps; defaults to the system clock.</param>
    public NotedXWebServer(
        ServerSettings settings,
        AppPaths paths,
        ILoggerFactory hostLoggerFactory,
        ILogger<NotedXWebServer> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _hostLoggerFactory = hostLoggerFactory ?? throw new ArgumentNullException(nameof(hostLoggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Whether Kestrel is currently listening.</summary>
    public bool IsRunning => _app is not null;

    /// <summary>The port the server actually bound to, or <c>0</c> when stopped.</summary>
    public int Port => _port;

    /// <summary>The base URL phones on the same network can use, without a trailing slash.</summary>
    public string? BaseUrl => _baseUrl;

    /// <summary>Whether the server is serving HTTPS with the self-signed certificate.</summary>
    public bool UsesHttps => _settings.UseHttps;

    /// <summary>The web application's services, available once the server has started.</summary>
    public IServiceProvider Services =>
        _app?.Services ?? throw new InvalidOperationException("The local server is not running.");

    /// <summary>Pairing grants exchanged by mobile devices.</summary>
    public PairingService Pairing =>
        Services.GetRequiredService<PairingService>();

    /// <summary>Pages and search, backed by the shared SQLite database.</summary>
    public PageService Pages =>
        Services.GetRequiredService<PageService>();

    /// <summary>The content-addressed file store.</summary>
    public BlobStore Blobs =>
        Services.GetRequiredService<BlobStore>();

    /// <summary>Imports and reads file attachments for the desktop UI.</summary>
    public AttachmentService Attachments =>
        Services.GetRequiredService<AttachmentService>();

    /// <summary>Lists and revokes paired mobile devices.</summary>
    public DeviceService Devices =>
        Services.GetRequiredService<DeviceService>();

    /// <summary>In-process and SignalR notifications about pages and transfers.</summary>
    public RealtimeNotifier Realtime =>
        Services.GetRequiredService<RealtimeNotifier>();

    /// <summary>Builds the URL a phone opens to redeem a pairing grant.</summary>
    public Uri BuildPairingUri(PairingSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_baseUrl is null)
        {
            throw new InvalidOperationException("The local server is not running.");
        }

        return new Uri($"{_baseUrl}/pair?t={Uri.EscapeDataString(session.Token)}");
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            return;
        }

        _paths.EnsureDirectories();

        var connectionString = BuildConnectionString(_paths);
        await InitializeDatabaseAsync(connectionString, cancellationToken);

        if (_settings.UseHttps)
        {
            _certificate = SelfSignedCertificateFactory.LoadOrCreate(
                _paths,
                [_settings.ServiceName],
                _logger);
        }

        var preferredPort = _settings.Port is >= 1 and <= 65535
            ? _settings.Port
            : ServerSettings.DefaultPort;
        var attempts = Math.Clamp(_settings.PortFallbackAttempts, 1, 100);
        Exception? lastError = null;

        for (var offset = 0; offset < attempts; offset++)
        {
            var candidate = preferredPort + offset;
            if (candidate > 65535)
            {
                break;
            }

            if (!PortUtility.IsFree(candidate))
            {
                _logger.LogDebug("Port {Port} is already in use; trying the next one.", candidate);
                continue;
            }

            var app = BuildApplication(candidate, connectionString);

            try
            {
                await app.StartAsync(cancellationToken);

                _app = app;
                _port = candidate;
                _baseUrl = BuildBaseUrl(candidate);

                _logger.LogInformation(
                    "Local network server listening at {BaseUrl} (HTTPS: {UsesHttps}).",
                    _baseUrl,
                    _settings.UseHttps);

                LogNetworkWarnings(candidate);
                await CleanupUploadsAsync(app, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsAddressInUse(ex))
            {
                lastError = ex;
                await app.DisposeAsync().ConfigureAwait(false);
                _logger.LogInformation(
                    "Port {Port} was claimed by another process; trying the next one.",
                    candidate);
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        _certificate?.Dispose();
        _certificate = null;

        _logger.LogError(lastError, "No free port was found near {Port}.", preferredPort);
        throw new InvalidOperationException(
            $"noted-x could not open a local network port between {preferredPort} and " +
            $"{Math.Min(65535, preferredPort + attempts - 1)}.",
            lastError);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var app = _app;
        if (app is null)
        {
            return;
        }

        _app = null;
        _baseUrl = null;
        _port = 0;

        try
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down; the server is disposed below either way.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The local network server stopped with an error.");
        }

        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The local network server could not be disposed cleanly.");
        }

        _certificate?.Dispose();
        _certificate = null;
        _logger.LogInformation("Local network server stopped.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing the local network server failed.");
        }
    }

    private WebApplication BuildApplication(int port, string connectionString)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(NotedXWebServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(_hostLoggerFactory));
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = Math.Max(
                16L * 1024 * 1024,
                (long)_settings.ChunkSizeBytes * 4);

            var address = ResolveBindAddress();
            kestrel.Listen(address, port, listen =>
            {
                if (_certificate is not null)
                {
                    listen.UseHttps(_certificate);
                }
            });
        });

        builder.Services.AddDbContextFactory<NotedXDbContext>(options =>
            options.UseSqlite(
                connectionString,
                sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

        builder.Services.AddSingleton(_timeProvider);
        builder.Services.AddSingleton(_paths);
        builder.Services.AddSingleton(_settings);
        builder.Services.AddSingleton<BlobStore>();
        builder.Services.AddSingleton<PageService>();
        builder.Services.AddSingleton<AttachmentService>();
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<DeviceService>();
        builder.Services.AddSingleton<UploadSessionStore>();
        builder.Services.AddSingleton<RealtimeConnectionRegistry>();
        builder.Services.AddSingleton<RealtimeNotifier>();

        builder.Services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumReceiveMessageSize = 64 * 1024;
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(45);
        });

        builder.Services
            .AddAuthentication(DeviceClaims.Scheme)
            .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(
                DeviceClaims.Scheme,
                static _ => { });
        builder.Services.AddAuthorization();

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PairingEndpoints.RateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, _settings.PairingAttemptsPerMinute),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.OnRejected = static (rejection, _) =>
            {
                if (rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    rejection.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                rejection.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return ValueTask.CompletedTask;
            };
        });

        builder.Services.AddProblemDetails();
        builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    private void ConfigurePipeline(WebApplication app)
    {
        app.UseExceptionHandler();

        app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;
            var response = http.Response;

            if (response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden &&
                http.Request.Path.StartsWithSegments("/api"))
            {
                var problem = new ProblemDetails
                {
                    Status = response.StatusCode,
                    Title = response.StatusCode == StatusCodes.Status401Unauthorized
                        ? "Not paired"
                        : "Forbidden",
                    Detail = response.StatusCode == StatusCodes.Status401Unauthorized
                        ? "Pair this device with the desktop app first."
                        : "This device may not perform that action."
                };

                await response.WriteAsJsonAsync(
                    problem,
                    options: null,
                    contentType: "application/problem+json",
                    cancellationToken: http.RequestAborted);
            }
        });

        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

            var path = context.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/pair"))
            {
                headers.CacheControl = "no-store";
                headers.Pragma = "no-cache";
            }

            await next(context);
        });

        app.UseResponseCompression();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        var webRoot = new ManifestEmbeddedFileProvider(
            MobileWebAssets.Assembly,
            MobileWebAssets.RootPath);

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRoot });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = webRoot,
            OnPrepareResponse = static context =>
            {
                if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(context.File.Name, "service-worker.js", StringComparison.OrdinalIgnoreCase))
                {
                    context.Context.Response.Headers.CacheControl = "no-cache";
                }
            }
        });

        app.MapHub<NotedXHub>("/hub").RequireAuthorization();
        app.MapPairingEndpoints();

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPageEndpoints();
        api.MapWorkspaceEndpoints();
        api.MapDeviceEndpoints();
        api.MapFileEndpoints();
        api.MapMethods(
            "/{**rest}",
            AllHttpMethods,
            () => ApiResults.NotFound("No such API endpoint."));

        RequestDelegate spa = context => ServeShellAsync(context, webRoot);
        app.MapFallback(spa);
    }

    private async Task InitializeDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<NotedXDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var factory = new TemporaryContextFactory(options);

        try
        {
            await DatabaseInitializer.InitializeAsync(factory, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Database ready at {DatabasePath}.", _paths.DatabaseFilePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The database at {DatabasePath} could not be prepared.", _paths.DatabaseFilePath);
            throw;
        }
    }

    private async Task CleanupUploadsAsync(WebApplication app, CancellationToken cancellationToken)
    {
        try
        {
            var uploads = app.Services.GetRequiredService<UploadSessionStore>();
            await uploads.CleanupStaleAsync(TimeSpan.FromHours(24), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stale uploads could not be cleaned up.");
        }
    }

    private void LogNetworkWarnings(int port)
    {
        var addresses = LanAddressProvider.GetAddresses();
        if (addresses.Count == 0)
        {
            _logger.LogWarning(
                "No local network address was found. Phones cannot reach this machine until it joins a Wi-Fi or Ethernet network.");
        }

        foreach (var address in addresses.Where(address => !address.IsPrivate))
        {
            _logger.LogWarning(
                "This machine has a public address ({Address}). noted-x is LAN-only; make sure port {Port} is blocked by the firewall or router.",
                address.Address,
                port);
        }

        if (addresses.Count > 0 && _baseUrl is not null)
        {
            _logger.LogInformation(
                "Phones on the same network can open {BaseUrl}/pair after scanning the desktop QR code.",
                _baseUrl);
        }
    }

    private IPAddress ResolveBindAddress()
    {
        if (IPAddress.TryParse(_settings.BindAddress, out var parsed))
        {
            return parsed;
        }

        if (!string.IsNullOrWhiteSpace(_settings.BindAddress))
        {
            _logger.LogWarning(
                "The configured bind address '{BindAddress}' is not valid; listening on all interfaces instead.",
                _settings.BindAddress);
        }

        return IPAddress.Any;
    }

    private string BuildBaseUrl(int port)
    {
        var scheme = _settings.UseHttps ? "https" : "http";
        var bind = ResolveBindAddress();
        var isWildcard = bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any);

        string host;
        if (isWildcard)
        {
            host = LanAddressProvider.GetPrimaryAddress()?.Address ?? "127.0.0.1";
        }
        else if (bind.AddressFamily == AddressFamily.InterNetworkV6)
        {
            host = $"[{bind}]";
        }
        else
        {
            host = bind.ToString();
        }

        return $"{scheme}://{host}:{port}";
    }

    private static string BuildConnectionString(AppPaths paths)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        return builder.ToString();
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ServeShellAsync(HttpContext context, IFileProvider webRoot)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var index = webRoot.GetFileInfo("index.html");
        if (!index.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.SendFileAsync(index, context.RequestAborted);
    }

    private sealed class TemporaryContextFactory(DbContextOptions<NotedXDbContext> options)
        : IDbContextFactory<NotedXDbContext>
    {
        public NotedXDbContext CreateDbContext() => new(options);

        public Task<NotedXDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
