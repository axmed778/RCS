using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rcs.Domain.Documents;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Documents;

namespace Rcs.UnitTests.Infrastructure;

public sealed class ContentStoreTests
{
    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public async Task StreamingLimitAcceptsExactBoundaryAndDiscardsOverrun(long length, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "rcs-store-tests", Guid.NewGuid().ToString("N"));
        var store = Create(root, 16);
        using var stream = new GeneratedStream(length);
        if (allowed)
        {
            var staged = await store.StageAsync(stream, CancellationToken.None);
            Assert.Equal(length, staged.ByteSize);
            store.DiscardStaged(staged);
        }
        else await Assert.ThrowsAsync<UploadTooLargeException>(() => store.StageAsync(stream, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "temp")));
        Assert.Empty(store.EnumerateObjects());
    }

    [Fact]
    public async Task Full500MegabyteBoundaryIsStreamedWithoutAFileSizedBuffer()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcs-store-tests", Guid.NewGuid().ToString("N"));
        var store = Create(root, FileTypePolicy.MaxUploadBytes);
        using var exact = new GeneratedStream(FileTypePolicy.MaxUploadBytes);
        var staged = await store.StageAsync(exact, CancellationToken.None);
        Assert.Equal(FileTypePolicy.MaxUploadBytes, staged.ByteSize);
        store.DiscardStaged(staged);
        using var over = new GeneratedStream(FileTypePolicy.MaxUploadBytes + 1);
        await Assert.ThrowsAsync<UploadTooLargeException>(() => store.StageAsync(over, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "temp")));
    }

    [Fact]
    public async Task InterruptedUploadNeverPublishesPartialBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcs-store-tests", Guid.NewGuid().ToString("N"));
        var store = Create(root, 1024);
        using var stream = new GeneratedStream(100, failAfter: 50);
        await Assert.ThrowsAsync<IOException>(() => store.StageAsync(stream, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "temp")));
        Assert.Empty(store.EnumerateObjects());
    }

    private static LocalContentStore Create(string root, long limit) => new(Options.Create(new StorageOptions
    {
        RootPath = Path.Combine(root, "objects"), TempPath = Path.Combine(root, "temp"), MaxUploadBytes = limit,
    }), NullLogger<LocalContentStore>.Instance);

    private sealed class GeneratedStream(long length, long? failAfter = null) : Stream
    {
        private long consumed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => consumed; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (failAfter is { } fail && consumed >= fail) throw new IOException("Synthetic interrupted stream");
            var take = (int)Math.Min(buffer.Length, length - consumed);
            if (failAfter is { } boundary) take = (int)Math.Min(take, boundary - consumed);
            buffer[..take].Clear(); consumed += take; return take;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
