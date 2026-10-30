using Microsoft.Extensions.Logging;

namespace NotedX.Server.Logging;

/// <summary>
/// Forwards the embedded web server's log entries to the desktop host's logger factory.
/// </summary>
/// <remarks>
/// The web application owns its own service provider; without forwarding, its categories
/// would not reach Serilog. The provider never disposes the target factory because the
/// desktop host owns its lifetime.
/// </remarks>
public sealed class ForwardingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory _target;

    /// <summary>Creates a provider that writes through <paramref name="target"/>.</summary>
    public ForwardingLoggerProvider(ILoggerFactory target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => _target.CreateLogger(categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // The desktop host owns the target factory.
    }
}
