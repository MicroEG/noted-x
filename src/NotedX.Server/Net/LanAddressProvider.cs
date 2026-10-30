using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NotedX.Server.Net;

/// <summary>Enumerates the IPv4 addresses on which the local network can reach this machine.</summary>
public static class LanAddressProvider
{
    /// <summary>Returns usable IPv4 addresses, best candidate for a phone on the same Wi-Fi first.</summary>
    public static IReadOnlyList<LanAddress> GetAddresses()
    {
        var results = new List<(LanAddress Address, int Score)>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var properties = networkInterface.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any));

            var virtualAdapter = IsVirtualAdapter(networkInterface.Name + " " + networkInterface.Description);
            var physicalType = networkInterface.NetworkInterfaceType is
                NetworkInterfaceType.Wireless80211 or
                NetworkInterfaceType.Ethernet or
                NetworkInterfaceType.GigabitEthernet or
                NetworkInterfaceType.FastEthernetT or
                NetworkInterfaceType.FastEthernetFx or
                NetworkInterfaceType.Ethernet3Megabit;

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var address = unicast.Address;
                if (IPAddress.IsLoopback(address) || IsLinkLocal(address))
                {
                    continue;
                }

                var score = 0;
                if (IsPrivate(address))
                {
                    score += 10;
                }

                if (hasGateway)
                {
                    score += 20;
                }

                if (physicalType)
                {
                    score += 6;
                }

                if (virtualAdapter)
                {
                    score -= 15;
                }

                results.Add((new LanAddress(address.ToString(), networkInterface.Name, IsPrivate(address)), score));
            }
        }

        return results
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Address.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Address.Address, StringComparer.Ordinal)
            .Select(entry => entry.Address)
            .ToArray();
    }

    /// <summary>Returns the best address for a phone on the same network, or <c>null</c>.</summary>
    public static LanAddress? GetPrimaryAddress() => GetAddresses().FirstOrDefault();

    /// <summary>Whether the address is a private, CGNAT, or link-local IPv4 address.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return true;
        }

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
               IsLinkLocal(address);
    }

    private static bool IsLinkLocal(IPAddress address) =>
        address.GetAddressBytes() is [169, 254, _, _];

    private static bool IsVirtualAdapter(string interfaceName)
    {
        string[] markers =
        [
            "vethernet",
            "virtual",
            "hyper-v",
            "wsl",
            "docker",
            "vmware",
            "virtualbox",
            "tailscale",
            "zerotier",
            "tap-",
            "vpn",
            "wi-fi direct"
        ];

        return markers.Any(marker => interfaceName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
