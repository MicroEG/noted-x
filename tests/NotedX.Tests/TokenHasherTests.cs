using NotedX.Core.Security;

namespace NotedX.Tests;

public sealed class TokenHasherTests
{
    [Fact]
    public void CreateToken_returns_url_safe_strings_of_the_expected_length()
    {
        var pairing = TokenHasher.CreateToken(TokenHasher.PairingTokenBytes);
        var device = TokenHasher.CreateToken(TokenHasher.DeviceTokenBytes);

        Assert.Equal(22, pairing.Length);
        Assert.Equal(43, device.Length);
        Assert.All(pairing, character =>
            Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        Assert.All(device, character =>
            Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    [Fact]
    public void CreateToken_rejects_weak_entropy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenHasher.CreateToken(15));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenHasher.CreateToken(0));
    }

    [Fact]
    public void CreateToken_produces_distinct_values()
    {
        var tokens = Enumerable
            .Range(0, 200)
            .Select(_ => TokenHasher.CreateToken())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(200, tokens.Count);
    }

    [Fact]
    public void CreatePin_produces_four_digits_by_default()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var pin = TokenHasher.CreatePin();

            Assert.Equal(4, pin.Length);
            Assert.All(pin, character => Assert.True(char.IsAsciiDigit(character)));
        }
    }

    [Fact]
    public void CreatePin_supports_custom_lengths_and_guards_the_range()
    {
        Assert.Equal(6, TokenHasher.CreatePin(6).Length);
        Assert.Equal(9, TokenHasher.CreatePin(9).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenHasher.CreatePin(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenHasher.CreatePin(10));
    }

    [Fact]
    public void Hash_matches_the_known_sha256_vector()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            TokenHasher.Hash("abc"));
        Assert.Equal(
            TokenHasher.Hash("abc"),
            Convert.ToHexString(TokenHasher.HashBytes("abc")).ToLowerInvariant());
        Assert.NotEqual(TokenHasher.Hash("abc"), TokenHasher.Hash("abd"));
    }

    [Fact]
    public void FixedTimeEquals_compares_without_accepting_nulls()
    {
        Assert.True(TokenHasher.FixedTimeEquals("secret", "secret"));
        Assert.False(TokenHasher.FixedTimeEquals("secret", "secreT"));
        Assert.False(TokenHasher.FixedTimeEquals("secret", "secret "));
        Assert.False(TokenHasher.FixedTimeEquals(string.Empty, "secret"));
        Assert.False(TokenHasher.FixedTimeEquals(null, "secret"));
        Assert.False(TokenHasher.FixedTimeEquals("secret", null));
        Assert.False(TokenHasher.FixedTimeEquals(null, null));
    }
}
