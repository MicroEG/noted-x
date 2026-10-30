using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NotedX.Core.Domain;
using NotedX.Core.Security;
using NotedX.Server;
using NotedX.Server.Security;

namespace NotedX.Tests;

public sealed class PairingServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateSession_issues_a_token_pin_and_default_expiry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(database, clock, new ServerSettings());

        var session = service.CreateSession();

        Assert.Equal(22, session.Token.Length);
        Assert.NotNull(session.Pin);
        Assert.Equal(4, session.Pin!.Length);
        Assert.All(session.Pin, character => Assert.True(char.IsAsciiDigit(character)));
        Assert.Equal(Start.AddMinutes(2), session.ExpiresAt);
    }

    [Fact]
    public async Task CreateSession_clamps_the_configured_lifetime()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(Start);

        var shortSettings = new ServerSettings { PairingTokenLifetime = TimeSpan.FromSeconds(5) };
        var shortSession = CreateService(database, clock, shortSettings).CreateSession();
        Assert.Equal(Start.AddSeconds(30), shortSession.ExpiresAt);

        var longSettings = new ServerSettings { PairingTokenLifetime = TimeSpan.FromHours(1) };
        var longSession = CreateService(database, clock, longSettings).CreateSession();
        Assert.Equal(Start.AddMinutes(15), longSession.ExpiresAt);
    }

    [Fact]
    public async Task RedeemAsync_requires_the_token_and_pin_then_persists_only_a_hash()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(database, clock, new ServerSettings());
        var session = service.CreateSession();

        Device? pairedDevice = null;
        var pairedEvents = 0;
        service.DevicePaired += (_, args) =>
        {
            pairedEvents++;
            pairedDevice = args.Device;
        };

        var wrongPin = session.Pin == "0000" ? "0001" : "0000";

        Assert.Equal(
            PairingStatus.InvalidToken,
            (await service.RedeemAsync("wrong-token", session.Pin, null)).Status);
        Assert.Equal(
            PairingStatus.PinRequired,
            (await service.RedeemAsync(session.Token, null, null)).Status);
        Assert.Equal(
            PairingStatus.PinRequired,
            (await service.RedeemAsync(session.Token, "   ", null)).Status);
        Assert.Equal(
            PairingStatus.InvalidPin,
            (await service.RedeemAsync(session.Token, wrongPin, null)).Status);
        Assert.Equal(0, pairedEvents);

        var result = await service.RedeemAsync(session.Token, session.Pin, "  Pixel 9  ");

        Assert.Equal(PairingStatus.Success, result.Status);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.DeviceToken);
        Assert.Equal(43, result.DeviceToken!.Length);
        Assert.NotNull(result.Device);
        Assert.Equal("Pixel 9", result.Device!.Name);
        Assert.Equal(Start, result.Device.CreatedAt);
        Assert.Equal(Start, result.Device.LastSeenAt);
        Assert.False(result.Device.IsRevoked);
        Assert.Equal(TokenHasher.Hash(result.DeviceToken), result.Device.TokenHash);
        Assert.Equal(1, pairedEvents);
        Assert.Same(result.Device, pairedDevice);

        await using var db = await database.CreateDbContextAsync();
        var stored = await db.Devices.SingleAsync();
        Assert.Equal(result.Device.Id, stored.Id);
        Assert.Equal(TokenHasher.Hash(result.DeviceToken), stored.TokenHash);
        Assert.DoesNotContain(result.DeviceToken, stored.TokenHash);
    }

    [Fact]
    public async Task RedeemAsync_is_single_use()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(database, clock, new ServerSettings());
        var session = service.CreateSession();

        var first = await service.RedeemAsync(session.Token, session.Pin, null);
        var second = await service.RedeemAsync(session.Token, session.Pin, null);

        Assert.True(first.Succeeded);
        Assert.Equal(PairingStatus.InvalidToken, second.Status);

        await using var db = await database.CreateDbContextAsync();
        Assert.Equal(1, await db.Devices.CountAsync());
    }

    [Fact]
    public async Task CreateSession_invalidates_the_previous_grant()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(database, clock, new ServerSettings());

        var first = service.CreateSession();
        var second = service.CreateSession();

        Assert.Equal(
            PairingStatus.InvalidToken,
            (await service.RedeemAsync(first.Token, first.Pin, null)).Status);

        Assert.True((await service.RedeemAsync(second.Token, second.Pin, null)).Succeeded);
    }

    [Fact]
    public async Task RedeemAsync_refuses_expired_grants()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(database, clock, new ServerSettings());
        var session = service.CreateSession();

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(
            PairingStatus.InvalidToken,
            (await service.RedeemAsync(session.Token, session.Pin, null)).Status);

        var fresh = service.CreateSession();
        Assert.True((await service.RedeemAsync(fresh.Token, fresh.Pin, null)).Succeeded);
    }

    [Fact]
    public async Task RedeemAsync_does_not_consume_the_grant_when_the_pin_is_wrong()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(database, clock, new ServerSettings());
        var session = service.CreateSession();
        var wrongPin = session.Pin == "0000" ? "0001" : "0000";

        Assert.Equal(
            PairingStatus.InvalidPin,
            (await service.RedeemAsync(session.Token, wrongPin, null)).Status);

        Assert.True((await service.RedeemAsync(session.Token, session.Pin, null)).Succeeded);
    }

    [Fact]
    public async Task RedeemAsync_skips_the_pin_when_it_is_disabled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(
            database,
            clock,
            new ServerSettings { RequirePairingPin = false });
        var session = service.CreateSession();

        Assert.Null(session.Pin);

        var result = await service.RedeemAsync(session.Token, null, "Tablet");

        Assert.True(result.Succeeded);
        Assert.Equal("Tablet", result.Device!.Name);
    }

    [Fact]
    public async Task RedeemAsync_normalizes_device_names()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(Start);
        var service = CreateService(database, clock, new ServerSettings());

        async Task<string> PairNameAsync(string? name)
        {
            var session = service.CreateSession();
            var result = await service.RedeemAsync(session.Token, session.Pin, name);
            Assert.True(result.Succeeded, "Pairing should succeed for the name test.");
            return result.Device!.Name;
        }

        Assert.Equal("Mobile device", await PairNameAsync(null));
        Assert.Equal("Mobile device", await PairNameAsync("   "));
        Assert.Equal("Mobile device", await PairNameAsync("\u0007\u0001"));
        Assert.Equal("Kitchen tablet", await PairNameAsync("  Kitchen tablet  "));

        var longName = await PairNameAsync(new string('a', 150));
        Assert.Equal(100, longName.Length);
    }

    private static PairingService CreateService(
        TestDatabase database,
        ManualTimeProvider clock,
        ServerSettings settings) =>
        new(database, settings, clock, NullLogger<PairingService>.Instance);
}
