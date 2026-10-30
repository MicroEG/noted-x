using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NotedX.Core.Security;

/// <summary>Creates opaque tokens and hashes them for storage.</summary>
/// <remarks>
/// Only hashes are persisted. Raw tokens never touch disk or logs. Comparisons use
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.
/// </remarks>
public static class TokenHasher
{
    /// <summary>Entropy used for long-lived device tokens.</summary>
    public const int DeviceTokenBytes = 32;

    /// <summary>Entropy used for short-lived pairing tokens.</summary>
    public const int PairingTokenBytes = 16;

    /// <summary>Creates a URL-safe random token.</summary>
    /// <param name="byteCount">Number of random bytes before encoding.</param>
    public static string CreateToken(int byteCount = DeviceTokenBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteCount, 16);

        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Creates a numeric PIN such as <c>0427</c>.</summary>
    /// <param name="digits">Number of digits to generate.</param>
    public static string CreatePin(int digits = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 9);

        var maximum = (int)Math.Pow(10, digits);
        var value = RandomNumberGenerator.GetInt32(maximum);
        return value.ToString("D" + digits, CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the lower-case hexadecimal SHA-256 hash of <paramref name="token"/>.</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexString(HashBytes(token)).ToLowerInvariant();
    }

    /// <summary>Returns the raw SHA-256 hash of <paramref name="token"/>.</summary>
    public static byte[] HashBytes(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Compares two secrets without leaking their length or content through timing.</summary>
    public static bool FixedTimeEquals(string? first, string? second)
    {
        if (first is null || second is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(first),
            Encoding.UTF8.GetBytes(second));
    }
}
