using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NotedX.Desktop.Services;

/// <summary>Outcome of a Windows Firewall rule request.</summary>
public enum FirewallRuleResult
{
    Succeeded,
    Cancelled,
    Failed,
    Unsupported
}

/// <summary>Adds inbound Windows Firewall rules for the local server after a UAC prompt.</summary>
public sealed class FirewallService
{
    private readonly ILogger<FirewallService> _logger;

    /// <summary>Creates the service.</summary>
    public FirewallService(ILogger<FirewallService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Creates private-network rules for the HTTP and discovery ports.</summary>
    /// <param name="tcpPort">Port the local web server listens on.</param>
    /// <param name="udpPort">Discovery port, or zero to skip the UDP rule.</param>
    /// <param name="cancellationToken">Cancels the wait for the elevated process.</param>
    public async Task<FirewallRuleResult> EnsureRulesAsync(
        int tcpPort,
        int udpPort,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return FirewallRuleResult.Unsupported;
        }

        if (tcpPort is < 1 or > 65535 || udpPort is < 0 or > 65535)
        {
            return FirewallRuleResult.Failed;
        }

        try
        {
            var script = BuildScript(tcpPort, udpPort);
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden " +
                    "-EncodedCommand " + encodedCommand,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogWarning("The elevated firewall process could not be started.");
                return FirewallRuleResult.Failed;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation(
                    "Windows Firewall rules were added for TCP {TcpPort} and UDP {UdpPort}.",
                    tcpPort,
                    udpPort);
                return FirewallRuleResult.Succeeded;
            }

            _logger.LogWarning("Windows Firewall rule creation failed with exit code {ExitCode}.", process.ExitCode);
            return FirewallRuleResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _logger.LogInformation("The user cancelled the Windows Firewall elevation prompt.");
            return FirewallRuleResult.Cancelled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows Firewall rules could not be created.");
            return FirewallRuleResult.Failed;
        }
    }

    private static string BuildScript(int tcpPort, int udpPort)
    {
        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");

        AppendRule(builder, $"noted-x TCP {tcpPort}", "TCP", tcpPort);

        if (udpPort is > 0 and <= 65535)
        {
            AppendRule(builder, $"noted-x discovery {udpPort}", "UDP", udpPort);
        }

        builder.AppendLine("exit $LASTEXITCODE");
        return builder.ToString();
    }

    private static void AppendRule(StringBuilder builder, string name, string protocol, int port)
    {
        var addRule =
            $"netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow " +
            $"protocol={protocol} localport={port} profile=private,domain";

        builder.AppendLine($"netsh advfirewall firewall delete rule name=\"{name}\" | Out-Null");
        builder.AppendLine(addRule);
    }
}
