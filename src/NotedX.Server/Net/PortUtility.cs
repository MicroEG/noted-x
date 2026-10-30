using System.Net;
using System.Net.Sockets;

namespace NotedX.Server.Net;

/// <summary>Helpers for locating a free TCP port.</summary>
public static class PortUtility
{
    /// <summary>Returns whether a TCP listener can bind to <paramref name="port"/> on all interfaces.</summary>
    public static bool IsFree(int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        try
        {
            var listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    /// <summary>Finds a free port at or after <paramref name="preferredPort"/>.</summary>
    /// <exception cref="InvalidOperationException">No free port exists in the requested range.</exception>
    public static int FindFreePort(int preferredPort, int attempts)
    {
        attempts = Math.Max(1, attempts);

        for (var offset = 0; offset < attempts; offset++)
        {
            var candidate = preferredPort + offset;
            if (candidate > 65535)
            {
                break;
            }

            if (IsFree(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No free TCP port was found between {preferredPort} and {Math.Min(65535, preferredPort + attempts - 1)}.");
    }
}
