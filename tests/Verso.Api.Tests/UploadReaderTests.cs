using Verso.Api.Uploads;

namespace Verso.Api.Tests;

/// <summary>
/// The upload endpoint's bounded reader (ADR 0023): it holds the file in memory once, never many times over, even when the
/// multipart section hands it 4 KB per read (as MultipartReader does), and stops at the limit + 1 byte.
/// </summary>
public sealed class UploadReaderTests
{
    private const int OneMb = 1_048_576;

    /// <summary>Hands out <paramref name="length"/> bytes of 'a', at most 4,096 per read, completing synchronously (so allocations stay on this thread).</summary>
    private sealed class SmallReads(long length, int readsBeforeFailure = int.MaxValue) : Stream
    {
        private long _position;
        private int _reads;

        public long BytesRead => _position;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_reads > readsBeforeFailure)
            {
                throw new IOException("Simulated client disconnect.");
            }

            var count = (int)Math.Min(Math.Min(buffer.Length, 4096), length - _position);
            buffer.Span[..count].Fill((byte)'a');
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); // the reader reads asynchronously
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Reading_a_file_in_4_KB_pieces_allocates_less_than_three_times_its_size()
    {
        await UploadEndpoints.ReadLimitedAsync(new SmallReads(64 * 1024), OneMb, CancellationToken.None); // warm up (JIT, pool)

        var before = GC.GetAllocatedBytesForCurrentThread();
        var content = await UploadEndpoints.ReadLimitedAsync(new SmallReads(OneMb), OneMb, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(content);
        Assert.Equal(OneMb, content.Length);
        Assert.All(content, b => Assert.Equal((byte)'a', b));
        Assert.True(allocated < 3L * OneMb, $"Allocated {allocated:N0} bytes to read a {OneMb:N0}-byte file.");
    }

    [Fact]
    public async Task Over_the_limit_stops_at_limit_plus_one_and_allocates_little()
    {
        var body = new SmallReads(10L * OneMb);
        await UploadEndpoints.ReadLimitedAsync(new SmallReads(2L * OneMb), OneMb, CancellationToken.None); // warm up

        var before = GC.GetAllocatedBytesForCurrentThread();
        var content = await UploadEndpoints.ReadLimitedAsync(body, OneMb, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(content);
        Assert.Equal(OneMb + 1, body.BytesRead);
        Assert.True(allocated < 3L * OneMb, $"Allocated {allocated:N0} bytes before refusing.");
    }

    [Fact]
    public async Task Small_file_and_empty_file_are_returned_exactly()
    {
        Assert.Equal(10_000, (await UploadEndpoints.ReadLimitedAsync(new SmallReads(10_000), OneMb, CancellationToken.None))!.Length);
        Assert.Empty((await UploadEndpoints.ReadLimitedAsync(new SmallReads(0), OneMb, CancellationToken.None))!);
        Assert.Equal(OneMb, (await UploadEndpoints.ReadLimitedAsync(new SmallReads(OneMb), OneMb, CancellationToken.None))!.Length);
    }

    [Fact]
    public async Task A_failing_read_propagates()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            UploadEndpoints.ReadLimitedAsync(new SmallReads(OneMb, readsBeforeFailure: 30), OneMb, CancellationToken.None));
    }
}
