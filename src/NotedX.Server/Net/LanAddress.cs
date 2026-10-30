namespace NotedX.Server.Net;

/// <summary>A usable IPv4 address of a local network interface.</summary>
/// <param name="Address">Dotted IPv4 address.</param>
/// <param name="InterfaceName">Human-readable interface description.</param>
/// <param name="IsPrivate">Whether the address belongs to an RFC 1918 / CGNAT / link-local range.</param>
public sealed record LanAddress(string Address, string InterfaceName, bool IsPrivate);
