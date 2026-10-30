using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NotedX.Core.Storage;

namespace NotedX.Tests;

public sealed class BlobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly BlobStore _store;

    public BlobStoreTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "notedx-tests",
            Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureDirectories();
        _store = new BlobStore(_paths, NullLogger<BlobStore>.Instance);
    }

    [Fact]
    public async Task SaveAsync_stores_content_and_returns_its_identity()
    {
        var payload = CreatePayload(4096);

        var result = await _store.SaveAsync(new MemoryStream(payload));

        Assert.False(result.AlreadyExisted);
        Assert.Equal((long)payload.Length, result.SizeBytes);
        Assert.Equal(ExpectedHash(payload), result.ContentHash);

        using var read = _store.OpenRead(result.ContentHash);

        Assert.NotNull(read);
        Assert.Equal((long)payload.Length, read!.TotalLength);
        Assert.Equal(0L, read.Offset);
        Assert.Equal((long)payload.Length, read.Length);
        Assert.Equal(payload, await ReadAllAsync(read.Content));
    }

    [Fact]
    public async Task SaveAsync_deduplicates_identical_content()
    {
        var payload = CreatePayload(1024);

        var first = await _store.SaveAsync(new MemoryStream(payload));
        var second = await _store.SaveAsync(new MemoryStream(payload));

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.ContentHash, second.ContentHash);

        var files = Directory.GetFiles(_paths.BlobsDirectory, "*", SearchOption.AllDirectories);
        Assert.Single(files);
    }

    [Fact]
    public async Task OpenRead_returns_only_the_requested_range()
    {
        var payload = CreatePayload(500);
        var saved = await _store.SaveAsync(new MemoryStream(payload));

        using var read = _store.OpenRead(saved.ContentHash, offset: 10, count: 25);

        Assert.NotNull(read);
        Assert.Equal(10L, read!.Offset);
        Assert.Equal(25L, read.Length);
        Assert.Equal((long)payload.Length, read.TotalLength);
        Assert.Equal(payload.AsSpan(10, 25).ToArray(), await ReadAllAsync(read.Content));
    }

    [Fact]
    public async Task OpenRead_clamps_counts_that_run_past_the_end()
    {
        var payload = CreatePayload(128);
        var saved = await _store.SaveAsync(new MemoryStream(payload));

        using var read = _store.OpenRead(saved.ContentHash, offset: 120, count: 100);

        Assert.NotNull(read);
        Assert.Equal(8L, read!.Length);
        Assert.Equal(payload.AsSpan(120).ToArray(), await ReadAllAsync(read.Content));
    }

    [Fact]
    public async Task OpenRead_supports_reading_from_the_end_and_full_length()
    {
        var payload = CreatePayload(64);
        var saved = await _store.SaveAsync(new MemoryStream(payload));

        using var atEnd = _store.OpenRead(saved.ContentHash, offset: payload.Length);

        Assert.NotNull(atEnd);
        Assert.Equal(0L, atEnd!.Length);
        Assert.Empty(await ReadAllAsync(atEnd.Content));

        using var whole = _store.OpenRead(saved.ContentHash);

        Assert.NotNull(whole);
        Assert.Equal((long)payload.Length, whole!.Length);
    }

    [Fact]
    public async Task OpenRead_returns_null_for_unknown_hash_and_validates_input()
    {
        var payload = CreatePayload(16);
        var saved = await _store.SaveAsync(new MemoryStream(payload));

        Assert.Null(_store.OpenRead(new string('0', 64)));
        Assert.Throws<ArgumentException>(() => _store.OpenRead("../../secrets.txt"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _store.OpenRead(saved.ContentHash, offset: payload.Length + 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _store.OpenRead(saved.ContentHash, offset: -1));
    }

    [Fact]
    public async Task OpenRead_accepts_upper_case_hashes()
    {
        var payload = CreatePayload(32);
        var saved = await _store.SaveAsync(new MemoryStream(payload));

        using var read = _store.OpenRead(saved.ContentHash.ToUpperInvariant());

        Assert.NotNull(read);
        Assert.Equal(payload, await ReadAllAsync(read!.Content));
    }

    [Fact]
    public async Task SaveAsync_accepts_non_seekable_streams()
    {
        var payload = CreatePayload(64 * 1024);
        using var source = new NonSeekableReadStream(new MemoryStream(payload));

        var result = await _store.SaveAsync(source);

        Assert.Equal(ExpectedHash(payload), result.ContentHash);
        Assert.Equal((long)payload.Length, result.SizeBytes);
    }

    [Fact]
    public async Task Bounded_stream_allows_seeking_inside_the_window()
    {
        var payload = CreatePayload(300);
        var saved = await _store.SaveAsync(new MemoryStream(payload));

        using var read = _store.OpenRead(saved.ContentHash, offset: 100, count: 150);

        Assert.NotNull(read);
        read!.Content.Seek(50, SeekOrigin.Begin);
        Assert.Equal(50L, read.Content.Position);

        var slice = await ReadAllAsync(read.Content);
        Assert.Equal(payload.AsSpan(150, 100).ToArray(), slice);
        Assert.Throws<IOException>(() => read.Content.Seek(151, SeekOrigin.Begin));
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
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

    private sealed class NonSeekableReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
