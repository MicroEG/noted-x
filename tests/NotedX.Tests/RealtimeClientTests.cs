using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using NotedX.Server.Api;
using NotedX.Server.Realtime;

namespace NotedX.Tests;

public sealed class RealtimeClientTests
{
    [Fact]
    public async Task Hub_rejects_connections_without_a_paired_token()
    {
        await using var harness = await ServerTestHarness.StartAsync();

        var connection = new HubConnectionBuilder()
            .WithUrl($"{harness.Client.BaseAddress}hub")
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Paired_clients_connect_and_receive_page_changes()
    {
        await using var harness = await ServerTestHarness.StartAsync();
        var pairing = await harness.PairAsync(deviceName: "Realtime phone");
        harness.Authorize(pairing.Token);

        var changes = new TaskCompletionSource<PageChangedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = BuildConnection(harness, pairing.Token);
        connection.On<JsonElement>("PageChanged", element =>
        {
            var change = element.Deserialize<PageChangedEvent>(ServerTestHarness.Json);
            if (change is not null)
            {
                changes.TrySetResult(change);
            }
        });

        await connection.StartAsync();
        try
        {
            var ping = await connection.InvokeAsync<string>("Ping");

            Assert.True(DateTimeOffset.TryParse(
                ping,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var serverTime));
            Assert.NotEqual(default, serverTime);

            var response = await harness.Client.PostAsJsonAsync(
                "/api/pages",
                new CreatePageRequest(null, null, "Realtime note", null, null),
                ServerTestHarness.Json);

            response.EnsureSuccessStatusCode();

            var page = await response.Content.ReadFromJsonAsync<PageDetailDto>(
                ServerTestHarness.Json);

            Assert.NotNull(page);

            var change = await changes.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(page!.Id, change.PageId);
            Assert.Equal("created", change.Change);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Uploads_broadcast_progress_and_completion_events()
    {
        await using var harness = await ServerTestHarness.StartAsync();
        var pairing = await harness.PairAsync(deviceName: "Upload phone");
        harness.Authorize(pairing.Token);

        var progress = new TaskCompletionSource<TransferProgressEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<TransferCompletedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = BuildConnection(harness, pairing.Token);
        connection.On<JsonElement>("TransferProgress", element =>
        {
            var payload = element.Deserialize<TransferProgressEvent>(ServerTestHarness.Json);
            if (payload is not null)
            {
                progress.TrySetResult(payload);
            }
        });
        connection.On<JsonElement>("TransferCompleted", element =>
        {
            var payload = element.Deserialize<TransferCompletedEvent>(ServerTestHarness.Json);
            if (payload is not null)
            {
                completion.TrySetResult(payload);
            }
        });

        await connection.StartAsync();
        try
        {
            var payload = Encoding.UTF8.GetBytes("noted-x realtime transfer");

            var initResponse = await harness.Client.PostAsJsonAsync(
                "/api/files",
                new CreateUploadRequest("notedx-test.txt", "text/plain", payload.Length, null),
                ServerTestHarness.Json);

            initResponse.EnsureSuccessStatusCode();

            var upload = await initResponse.Content.ReadFromJsonAsync<UploadInitResponse>(
                ServerTestHarness.Json);

            Assert.NotNull(upload);
            Assert.Equal(payload.Length, upload!.SizeBytes);

            using var request = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl)
            {
                Content = new ByteArrayContent(payload)
            };
            request.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(0, payload.Length - 1, payload.Length);

            var uploadResponse = await harness.Client.SendAsync(request);

            uploadResponse.EnsureSuccessStatusCode();

            var completed = await uploadResponse.Content.ReadFromJsonAsync<UploadChunkResponse>(
                ServerTestHarness.Json);

            Assert.NotNull(completed);
            Assert.True(completed!.Completed);
            Assert.NotNull(completed.AttachmentId);

            var progressEvent = await progress.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(upload.UploadId, progressEvent.UploadId);
            Assert.Equal("notedx-test.txt", progressEvent.FileName);
            Assert.Equal(payload.Length, progressEvent.ReceivedBytes);
            Assert.Equal(payload.Length, progressEvent.TotalBytes);
            Assert.Equal("Upload phone", progressEvent.DeviceName);

            var completedEvent = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(upload.UploadId, completedEvent.UploadId);
            Assert.Equal(completed.AttachmentId, completedEvent.AttachmentId);
            Assert.Equal(completed.ContentHash, completedEvent.ContentHash);
            Assert.Equal("Upload phone", completedEvent.DeviceName);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private static HubConnection BuildConnection(ServerTestHarness harness, string token) =>
        new HubConnectionBuilder()
            .WithUrl($"{harness.Client.BaseAddress}hub", options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(token))
            .Build();
}
