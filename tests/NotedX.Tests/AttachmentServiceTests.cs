using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NotedX.Core.Domain;
using NotedX.Core.Notes;
using NotedX.Core.Storage;

namespace NotedX.Tests;

public sealed class AttachmentServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ImportAsync_stores_the_blob_and_records_metadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var payload = CreatePayload(2048);
        var source = await fixture.WriteSourceAsync("photo.png", payload);

        var attachment = await fixture.Attachments.ImportAsync(source);

        Assert.Equal("photo.png", attachment.FileName);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal((long)payload.Length, attachment.SizeBytes);
        Assert.Equal("desktop", attachment.UploadedBy);
        Assert.Null(attachment.PageId);
        Assert.Equal(Start, attachment.CreatedAt);
        Assert.Equal(ExpectedHash(payload), attachment.BlobHash);

        using var read = fixture.Blobs.OpenRead(attachment.BlobHash);

        Assert.NotNull(read);
        Assert.Equal((long)payload.Length, read!.Length);
        Assert.Equal(payload, await ReadAllAsync(read.Content));

        var loaded = await fixture.Attachments.GetAsync(attachment.Id);

        Assert.NotNull(loaded);
        Assert.Equal(attachment.BlobHash, loaded!.BlobHash);
    }

    [Fact]
    public async Task ImportAsync_links_attachments_to_live_pages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreatePageAsync("Files");
        var source = await fixture.WriteSourceAsync("notes.md", CreatePayload(64));

        var attachment = await fixture.Attachments.ImportAsync(source, page.Id, "  Pixel  ");

        Assert.Equal(page.Id, attachment.PageId);
        Assert.Equal("Pixel", attachment.UploadedBy);
        Assert.Equal("text/markdown", attachment.MimeType);

        var forPage = await fixture.Attachments.ListForPageAsync(page.Id);

        Assert.Single(forPage);
        Assert.Equal(attachment.Id, forPage[0].Id);
        Assert.Single(await fixture.Attachments.ListAllAsync());
    }

    [Fact]
    public async Task ImportAsync_deduplicates_identical_content()
    {
        await using var fixture = await Fixture.CreateAsync();
        var payload = CreatePayload(1024);
        var first = await fixture.Attachments.ImportAsync(
            await fixture.WriteSourceAsync("one.bin", payload));
        var second = await fixture.Attachments.ImportAsync(
            await fixture.WriteSourceAsync("two.bin", payload));

        Assert.Equal(first.BlobHash, second.BlobHash);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await fixture.Attachments.ListAllAsync()).Count);
        Assert.Single(Directory.GetFiles(
            fixture.Database.Paths.BlobsDirectory,
            "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ImportAsync_rejects_missing_files_and_unknown_pages()
    {
        await using var fixture = await Fixture.CreateAsync();

        var missing = Path.Combine(fixture.Database.Root, "imports", "missing.txt");
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => fixture.Attachments.ImportAsync(missing));

        var source = await fixture.WriteSourceAsync("orphan.txt", CreatePayload(8));
        await Assert.ThrowsAsync<PageValidationException>(
            () => fixture.Attachments.ImportAsync(source, Guid.NewGuid()));

        var page = await fixture.CreatePageAsync("Trashed");
        await fixture.Pages.SoftDeleteAsync(page.Id);
        await Assert.ThrowsAsync<PageValidationException>(
            () => fixture.Attachments.ImportAsync(source, page.Id));

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Attachments.ImportAsync("   "));
    }

    [Fact]
    public async Task ListAllAsync_orders_attachments_newest_first()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreatePageAsync("Timeline");

        var older = await fixture.Attachments.ImportAsync(
            await fixture.WriteSourceAsync("older.txt", CreatePayload(10)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var newer = await fixture.Attachments.ImportAsync(
            await fixture.WriteSourceAsync("newer.txt", CreatePayload(11)),
            page.Id);

        var all = await fixture.Attachments.ListAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal(newer.Id, all[0].Id);
        Assert.Equal(older.Id, all[1].Id);

        var forPage = await fixture.Attachments.ListForPageAsync(page.Id);

        Assert.Single(forPage);
        Assert.Equal(newer.Id, forPage[0].Id);
    }

    [Fact]
    public async Task DeleteAsync_removes_the_record_but_keeps_the_blob()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attachment = await fixture.Attachments.ImportAsync(
            await fixture.WriteSourceAsync("keep.txt", CreatePayload(32)));

        Assert.True(await fixture.Attachments.DeleteAsync(attachment.Id));
        Assert.False(await fixture.Attachments.DeleteAsync(attachment.Id));
        Assert.Empty(await fixture.Attachments.ListAllAsync());
        Assert.Null(await fixture.Attachments.GetAsync(attachment.Id));

        using var read = fixture.Blobs.OpenRead(attachment.BlobHash);

        Assert.NotNull(read);
        Assert.Equal(32, (int)read!.Length);
    }

    [Theory]
    [InlineData("photo.png", "image/png")]
    [InlineData("PHOTO.JPG", "image/jpeg")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("archive.tar.gz", "application/octet-stream")]
    [InlineData("no-extension", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    [InlineData("  ", "application/octet-stream")]
    public void GuessMimeType_maps_extensions_case_insensitively(
        string? fileName,
        string expected)
    {
        Assert.Equal(expected, AttachmentService.GuessMimeType(fileName));
    }

    [Fact]
    public async Task ImportAsync_normalizes_the_uploader()
    {
        await using var fixture = await Fixture.CreateAsync();

        var blank = await fixture.Attachments.ImportAsync(
            await fixture.WriteSourceAsync("blank.txt", CreatePayload(4)),
            uploadedBy: "   ");

        Assert.Equal("desktop", blank.UploadedBy);

        var longName = await fixture.Attachments.ImportAsync(
            await fixture.WriteSourceAsync("long.txt", CreatePayload(5)),
            uploadedBy: new string('u', 150));

        Assert.Equal(100, longName.UploadedBy.Length);
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }

    private static string ExpectedHash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(TestDatabase database)
        {
            Database = database;
            Clock = new ManualTimeProvider(Start);
            Blobs = new BlobStore(database.Paths, NullLogger<BlobStore>.Instance);
            Pages = new PageService(database, NullLogger<PageService>.Instance, Clock);
            Attachments = new AttachmentService(database, Blobs, Clock);
            Directory.CreateDirectory(ImportsDirectory);
        }

        public TestDatabase Database { get; }

        public ManualTimeProvider Clock { get; }

        public BlobStore Blobs { get; }

        public PageService Pages { get; }

        public AttachmentService Attachments { get; }

        private string ImportsDirectory => Path.Combine(Database.Root, "imports");

        public static async Task<Fixture> CreateAsync() =>
            new(await TestDatabase.CreateAsync());

        public async Task<string> WriteSourceAsync(string fileName, byte[] payload)
        {
            var path = Path.Combine(ImportsDirectory, fileName);
            await File.WriteAllBytesAsync(path, payload);
            return path;
        }

        public Task<Page> CreatePageAsync(string title) =>
            Pages.CreateAsync(new PageDraft(null, null, title, null, null));

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
