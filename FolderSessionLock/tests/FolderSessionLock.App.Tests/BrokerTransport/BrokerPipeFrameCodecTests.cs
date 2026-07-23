using System.Buffers.Binary;
using System.Text;
using FolderSessionLock.Broker.Transport;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Transport.Tests;

public sealed class BrokerPipeFrameCodecTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(1)]
    [InlineData(BrokerPipeEndpoint.MaximumBodyLength)]
    public async Task WriteAndReadAsync_PreserveBoundaryLengthAndLittleEndianPrefix(int bodyLength)
    {
        byte[] body = Enumerable.Repeat((byte)'a', bodyLength).ToArray();
        await using var stream = new MemoryStream();

        await BrokerPipeFrameCodec.WriteAsync(stream, body);
        byte[] frame = stream.ToArray();

        Assert.Equal(checked((uint)bodyLength), BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(0, 4)));
        Assert.Equal((byte)(bodyLength & 0xff), frame[0]);
        stream.Position = 0;
        BrokerPipeReadResult result = await BrokerPipeFrameCodec.ReadAsync(stream, ReadTimeout);
        Assert.True(result.IsSuccess);
        Assert.Equal(body, result.Body.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(BrokerPipeEndpoint.MaximumBodyLength + 1)]
    public async Task ReadAsync_RejectsZeroAndOversizedDeclarations(int declaredLength)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)declaredLength));
        await using var stream = new MemoryStream(prefix);

        BrokerPipeReadResult result = await BrokerPipeFrameCodec.ReadAsync(stream, ReadTimeout);

        AssertError(result, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
    }

    [Fact]
    public async Task WriteAsync_RejectsZeroAndOversizedBodies()
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await BrokerPipeFrameCodec.WriteAsync(stream, ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await BrokerPipeFrameCodec.WriteAsync(
                stream,
                new byte[BrokerPipeEndpoint.MaximumBodyLength + 1]));
    }

    [Fact]
    public async Task ReadAsync_RejectsIncompletePrefixAndBody()
    {
        await using var prefixStream = new MemoryStream([1, 0, 0]);
        await using var bodyStream = new MemoryStream(Frame(Encoding.UTF8.GetBytes("{}"))[..^1]);

        BrokerPipeReadResult prefix = await BrokerPipeFrameCodec.ReadAsync(prefixStream, ReadTimeout);
        BrokerPipeReadResult body = await BrokerPipeFrameCodec.ReadAsync(bodyStream, ReadTimeout);

        AssertError(prefix, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
        AssertError(body, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
    }

    [Fact]
    public async Task ReadAsync_ReturnsOneFrameAndReportsTrailingData()
    {
        byte[] frame = Frame(Encoding.UTF8.GetBytes("{}"));
        await using var trailingByte = new MemoryStream([.. frame, 0]);
        await using var secondFrame = new MemoryStream([.. frame, .. frame]);

        BrokerPipeReadResult trailing = await BrokerPipeFrameCodec.ReadAsync(trailingByte, ReadTimeout);
        BrokerPipeReadResult second = await BrokerPipeFrameCodec.ReadAsync(secondFrame, ReadTimeout);

        Assert.True(trailing.IsSuccess);
        Assert.True(trailing.HasTrailingData);
        Assert.True(second.IsSuccess);
        Assert.True(second.HasTrailingData);
    }

    [Fact]
    public async Task ReadAsync_RejectsUtf8BomAndInvalidUtf8()
    {
        await using var bom = new MemoryStream(Frame([0xef, 0xbb, 0xbf, (byte)'{', (byte)'}']));
        await using var invalid = new MemoryStream(Frame([0xc3, 0x28]));

        BrokerPipeReadResult bomResult = await BrokerPipeFrameCodec.ReadAsync(bom, ReadTimeout);
        BrokerPipeReadResult invalidResult = await BrokerPipeFrameCodec.ReadAsync(invalid, ReadTimeout);

        AssertError(bomResult, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
        AssertError(invalidResult, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
    }

    [Fact]
    public async Task ReadAsync_MapsCallerCancellationAndReadTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var cancelledStream = new NeverCompletingReadStream();
        await using var timeoutStream = new NeverCompletingReadStream();

        BrokerPipeReadResult cancelled = await BrokerPipeFrameCodec.ReadAsync(
            cancelledStream,
            ReadTimeout,
            cancellation.Token);
        BrokerPipeReadResult timedOut = await BrokerPipeFrameCodec.ReadAsync(
            timeoutStream,
            TimeSpan.FromMilliseconds(50));

        AssertError(cancelled, BrokerErrorCodes.FSL_E_OPERATION_CANCELLED);
        AssertError(timedOut, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
        Assert.False(cancelled.TimedOut);
        Assert.True(timedOut.TimedOut);
    }

    [Fact]
    public async Task ReadAsync_MapsUnauthorizedAccess()
    {
        await using var stream = new UnauthorizedReadStream();

        BrokerPipeReadResult result = await BrokerPipeFrameCodec.ReadAsync(stream, ReadTimeout);

        AssertError(result, BrokerErrorCodes.FSL_E_PIPE_ACCESS_DENIED);
    }

    internal static byte[] Frame(ReadOnlySpan<byte> body)
    {
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)body.Length));
        body.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static void AssertError(BrokerPipeReadResult result, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.True(result.Body.IsEmpty);
        Assert.Equal(code, result.Error!.Code);
    }

    private class NeverCompletingReadStream : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnauthorizedReadStream : NeverCompletingReadStream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new UnauthorizedAccessException());
    }
}
