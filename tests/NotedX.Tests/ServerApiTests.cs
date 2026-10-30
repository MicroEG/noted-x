using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NotedX.Core.Notes;
using NotedX.Server.Api;

namespace NotedX.Tests;

public sealed class ServerApiTests
{
    private static readonly JsonSerializerOptions Json = ServerTestHarness.Json;

    [Fact]
    public async Task Pairing_grants_a_single_use_token_and_unlocks_the_api()
    {
        await using var harness = await ServerTestHarness.StartAsync();

        var anonymous = await harness.Client.GetAsync("/api/pages");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("application/problem+json", anonymous.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", anonymous.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", anonymous.Headers.CacheControl?.ToString());

        var session = harness.Server.Pairing.CreateSession();

        var missingPin = await harness.Client.PostAsJsonAsync(
            "/pair",
            new PairRequest(session.Token, null, "Test phone"),
            Json);
        Assert.Equal(HttpStatusCode.Unauthorized, missingPin.StatusCode);

        var wrongPin = session.Pin == "0000" ? "1111" : "0000";
        var rejected = await harness.Client.PostAsJsonAsync(
            "/pair",
            new PairRequest(session.Token, wrongPin, "Test phone"),
            Json);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        var paired = await harness.Client.PostAsJsonAsync(
            "/pair",
            new PairRequest(session.Token, session.Pin, "Test phone"),
            Json);
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);

        var grant = (await paired.Content.ReadFromJsonAsync<PairResponse>(Json))!;
        Assert.False(string.IsNullOrWhiteSpace(grant.Token));
        Assert.Equal("Test phone", grant.DeviceName);

        var reused = await harness.Client.PostAsJsonAsync(
            "/pair",
            new PairRequest(session.Token, session.Pin, "Test phone"),
            Json);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        harness.Authorize(grant.Token);
        var pages = await harness.Client.GetAsync("/api/pages");
        Assert.Equal(HttpStatusCode.OK, pages.StatusCode);

        var devicesResponse = await harness.Client.GetAsync("/api/devices");
        var devicesBody = await devicesResponse.Content.ReadAsStringAsync();
        Assert.True(devicesResponse.IsSuccessStatusCode, devicesBody);

        var devices = JsonSerializer.Deserialize<DeviceDto[]>(devicesBody, Json);
        var device = Assert.Single(devices!);
        Assert.Equal("Test phone", device.Name);
        Assert.False(device.IsRevoked);
    }

    [Fact]
    public async Task Expired_pairing_grants_are_rejected()
    {
        await using var harness = await ServerTestHarness.StartAsync(
            settings => settings.RequirePairingPin = false);

        var session = harness.Server.Pairing.CreateSession();
        harness.Clock.Advance(TimeSpan.FromMinutes(3));

        var response = await harness.Client.PostAsJsonAsync(
            "/pair",
            new PairRequest(session.Token, null, "Test phone"),
            Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pairing_is_rate_limited_per_address()
    {
        await using var harness = await ServerTestHarness.StartAsync(settings =>
        {
            settings.RequirePairingPin = false;
            settings.PairingAttemptsPerMinute = 3;
        });

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = await harness.Client.PostAsJsonAsync(
                "/pair",
                new PairRequest("not-a-real-token", null, "Test phone"),
                Json);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [
                HttpStatusCode.Unauthorized,
                HttpStatusCode.Unauthorized,
                HttpStatusCode.Unauthorized,
                HttpStatusCode.TooManyRequests
            ],
            statuses);
    }

    [Fact]
    public async Task Revoking_a_device_invalidates_its_token()
    {
        await using var harness = await ServerTestHarness.StartAsync(
            settings => settings.RequirePairingPin = false);

        var grant = await harness.PairAsync();
        harness.Authorize(grant.Token);

        var devices = await harness.Client.GetFromJsonAsync<DeviceDto[]>("/api/devices", Json);
        var device = Assert.Single(devices!);

        var revoke = await harness.Client.DeleteAsync($"/api/devices/{device.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var afterRevoke = await harness.Client.GetAsync("/api/pages");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task Pages_can_be_created_updated_searched_trashed_and_restored()
    {
        await using var harness = await ServerTestHarness.StartAsync(
            settings => settings.RequirePairingPin = false);

        var grant = await harness.PairAsync();
        harness.Authorize(grant.Token);

        var create = await harness.Client.PostAsJsonAsync(
            "/api/pages",
            new CreatePageRequest(
                null,
                null,
                "Harbor trip",
                "boat",
                [
                    new BlockInput(null, "heading1", "{\"text\":\"Itinerary\"}"),
                    new BlockInput(null, "paragraph", "{\"text\":\"Visit the harbor at sunrise.\"}")
                ]),
            Json);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.IsSuccessStatusCode, createBody);
        var page = JsonSerializer.Deserialize<PageDetailDto>(createBody, Json)!;
        Assert.Equal("Harbor trip", page.Title);
        Assert.Equal(2, page.Blocks.Count);
        Assert.Equal("heading1", page.Blocks[0].Type);
        Assert.Equal("paragraph", page.Blocks[1].Type);

        var fetched = await harness.Client.GetFromJsonAsync<PageDetailDto>($"/api/pages/{page.Id}", Json);
        Assert.Equal(page.Id, fetched!.Id);
        Assert.Equal("boat", fetched.Icon);

        var update = await harness.Client.PutAsJsonAsync(
            $"/api/pages/{page.Id}",
            new UpdatePageRequest(
                "Harbor sunrise",
                null,
                true,
                [
                    new BlockInput(null, "paragraph", "{\"text\":\"The harbor is calm before seven.\"}"),
                    new BlockInput(null, "toDo", "{\"text\":\"Buy ferry tickets\",\"checked\":false}")
                ],
                ["travel"]),
            Json);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<PageDetailDto>(Json))!;
        Assert.Equal("Harbor sunrise", updated.Title);
        Assert.True(updated.IsFavorite);
        Assert.Equal(2, updated.Blocks.Count);
        Assert.Equal("toDo", updated.Blocks[1].Type);
        var tag = Assert.Single(updated.Tags);
        Assert.Equal("travel", tag.Name);

        var hits = await harness.Client.GetFromJsonAsync<PageSearchHit[]>("/api/search?q=ferry", Json);
        var hit = Assert.Single(hits!);
        Assert.Equal(page.Id, hit.Id);

        var revisions = await harness.Client.GetFromJsonAsync<RevisionDto[]>(
            $"/api/pages/{page.Id}/revisions",
            Json);
        Assert.Single(revisions!);

        var trash = await harness.Client.DeleteAsync($"/api/pages/{page.Id}");
        Assert.Equal(HttpStatusCode.NoContent, trash.StatusCode);

        var live = await harness.Client.GetFromJsonAsync<PageSummaryDto[]>("/api/pages", Json);
        Assert.Empty(live!);

        var trashHits = await harness.Client.GetFromJsonAsync<PageSearchHit[]>("/api/search?q=ferry", Json);
        Assert.Empty(trashHits!);

        var trashed = await harness.Client.GetFromJsonAsync<PageSummaryDto[]>(
            "/api/pages?includeDeleted=true",
            Json);
        Assert.Contains(trashed!, item => item.Id == page.Id && item.IsDeleted);

        var restore = await harness.Client.PostAsync($"/api/pages/{page.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);

        var restoredHits = await harness.Client.GetFromJsonAsync<PageSearchHit[]>("/api/search?q=ferry", Json);
        Assert.Single(restoredHits!);
    }

    [Fact]
    public async Task Files_can_be_uploaded_in_chunks_resumed_and_range_downloaded()
    {
        await using var harness = await ServerTestHarness.StartAsync(
            settings => settings.RequirePairingPin = false);

        var grant = await harness.PairAsync();
        harness.Authorize(grant.Token);

        var payload = CreatePayload(300_000);
        var init = await InitUploadAsync(harness, payload.Length, "backup.bin");
        Assert.Equal(256 * 1024, init.ChunkSize);
        Assert.Equal(payload.Length, init.SizeBytes);

        var firstLength = init.ChunkSize;
        var firstResponse = await SendChunkAsync(
            harness,
            init.UploadId,
            from: 0,
            payload[..firstLength],
            payload.Length);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = (await firstResponse.Content.ReadFromJsonAsync<UploadChunkResponse>(Json))!;
        Assert.False(first.Completed);
        Assert.Equal(firstLength, first.ReceivedBytes);

        var status = await harness.Client.GetFromJsonAsync<UploadStatusResponse>(
            $"/api/uploads/{init.UploadId}",
            Json);
        Assert.Equal(firstLength, status!.ReceivedBytes);
        Assert.False(status.CanComplete);

        var secondResponse = await SendChunkAsync(
            harness,
            init.UploadId,
            firstLength,
            payload[firstLength..],
            payload.Length);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = (await secondResponse.Content.ReadFromJsonAsync<UploadChunkResponse>(Json))!;
        Assert.True(second.Completed);
        Assert.NotNull(second.AttachmentId);
        Assert.Equal(64, second.ContentHash?.Length);

        var attachmentId = second.AttachmentId!.Value;
        var meta = await harness.Client.GetFromJsonAsync<AttachmentDto>(
            $"/api/files/{attachmentId}/meta",
            Json);
        Assert.Equal("backup.bin", meta!.FileName);
        Assert.Equal(payload.Length, meta.SizeBytes);
        Assert.Equal(second.ContentHash, meta.ContentHash);

        var download = await harness.Client.GetAsync($"/api/files/{attachmentId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());

        var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{attachmentId}");
        rangeRequest.Headers.Range = new RangeHeaderValue(10, 19);
        var rangeResponse = await harness.Client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal(payload[10..20], await rangeResponse.Content.ReadAsByteArrayAsync());

        var outOfOrderInit = await InitUploadAsync(harness, 10, "tiny.bin");
        var outOfOrder = await SendChunkAsync(
            harness,
            outOfOrderInit.UploadId,
            from: 5,
            new byte[5],
            total: 10);

        Assert.Equal(HttpStatusCode.Conflict, outOfOrder.StatusCode);
        var outOfOrderStatus = await outOfOrder.Content.ReadFromJsonAsync<UploadStatusResponse>(Json);
        Assert.Equal(0, outOfOrderStatus!.ReceivedBytes);

        var duplicate = await UploadFileAsync(harness, payload, "copy.bin");
        Assert.Equal(second.ContentHash, duplicate.ContentHash);

        var blobFiles = Directory
            .EnumerateFiles(harness.Paths.BlobsDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}.staging{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .ToArray();

        var blob = Assert.Single(blobFiles);
        Assert.Equal(second.ContentHash, Path.GetFileName(blob));
    }

    [Fact]
    public async Task Realtime_hub_and_pwa_shell_have_the_expected_access_rules()
    {
        await using var harness = await ServerTestHarness.StartAsync(
            settings => settings.RequirePairingPin = false);

        var shell = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal("text/html", shell.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", shell.Headers.CacheControl?.ToString());

        var pairingPage = await harness.Client.GetAsync("/pair?t=abc");
        Assert.Equal(HttpStatusCode.OK, pairingPage.StatusCode);

        var unknownApi = await harness.Client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.Unauthorized, unknownApi.StatusCode);

        var anonymousHub = await harness.Client.PostAsync("/hub/negotiate?negotiateVersion=1", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousHub.StatusCode);

        var grant = await harness.PairAsync();
        harness.Authorize(grant.Token);

        var authorizedHub = await harness.Client.PostAsync("/hub/negotiate?negotiateVersion=1", content: null);
        Assert.Equal(HttpStatusCode.OK, authorizedHub.StatusCode);
        Assert.Contains("connectionToken", await authorizedHub.Content.ReadAsStringAsync());

        var missingEndpoint = await harness.Client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missingEndpoint.StatusCode);
    }

    private static async Task<UploadInitResponse> InitUploadAsync(
        ServerTestHarness harness,
        long sizeBytes,
        string fileName)
    {
        var response = await harness.Client.PostAsJsonAsync(
            "/api/files",
            new CreateUploadRequest(fileName, "application/octet-stream", sizeBytes, null),
            Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UploadInitResponse>(Json))!;
    }

    private static Task<HttpResponseMessage> SendChunkAsync(
        ServerTestHarness harness,
        Guid uploadId,
        long from,
        byte[] chunk,
        long total)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/files/{uploadId}")
        {
            Content = new ByteArrayContent(chunk)
        };

        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(
            from,
            from + chunk.Length - 1,
            total);

        return harness.Client.SendAsync(request);
    }

    private static async Task<UploadChunkResponse> UploadFileAsync(
        ServerTestHarness harness,
        byte[] payload,
        string fileName)
    {
        var init = await InitUploadAsync(harness, payload.Length, fileName);
        UploadChunkResponse? last = null;
        var offset = 0;

        while (offset < payload.Length)
        {
            var length = Math.Min(init.ChunkSize, payload.Length - offset);
            var response = await SendChunkAsync(
                harness,
                init.UploadId,
                offset,
                payload.AsSpan(offset, length).ToArray(),
                payload.Length);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            last = await response.Content.ReadFromJsonAsync<UploadChunkResponse>(Json);
            offset += length;
        }

        Assert.NotNull(last);
        Assert.True(last!.Completed);
        return last;
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        new Random(length).NextBytes(payload);
        return payload;
    }
}
