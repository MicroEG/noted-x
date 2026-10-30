namespace NotedX.Server.Security;

/// <summary>A short-lived pairing grant shown on the desktop as a QR code and PIN.</summary>
/// <param name="Token">Secret embedded in the QR URL. Never logged.</param>
/// <param name="Pin">Optional four-digit confirmation PIN. Never logged.</param>
/// <param name="ExpiresAt">UTC instant after which the grant is refused.</param>
public sealed record PairingSession(string Token, string? Pin, DateTimeOffset ExpiresAt);
