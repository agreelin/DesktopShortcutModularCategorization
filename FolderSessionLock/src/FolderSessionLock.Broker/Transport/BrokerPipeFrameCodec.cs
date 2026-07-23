using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using FolderSessionLock.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Transport;

public sealed record BrokerPipeReadResult(
    ReadOnlyMemory<byte> Body,
    BrokerError? Error,
    bool HasTrailingData = false,
    bool TimedOut = false)
{
    public bool IsSuccess => Error is null;
}

public static class BrokerPipeFrameCodec
{
    private const int ErrorBrokenPipe = 0x6d;
    private const int ErrorNoData = 0xe8;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async ValueTask<BrokerPipeReadResult> ReadAsync(
        Stream stream,
        TimeSpan readTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (readTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readTimeout));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(readTimeout);

        try
        {
            byte[] prefix = new byte[BrokerPipeEndpoint.LengthPrefixSize];
            if (!await ReadExactlyAsync(stream, prefix, timeoutSource.Token).ConfigureAwait(false))
            {
                return Malformed();
            }

            uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
            if (declaredLength is < BrokerPipeEndpoint.MinimumBodyLength
                or > BrokerPipeEndpoint.MaximumBodyLength)
            {
                return Malformed();
            }

            byte[] body = new byte[declaredLength];
            if (!await ReadExactlyAsync(stream, body, timeoutSource.Token).ConfigureAwait(false)
                || HasUtf8Bom(body)
                || !IsStrictUtf8(body))
            {
                return Malformed();
            }

            return new BrokerPipeReadResult(body, null, HasAvailableData(stream));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new BrokerPipeReadResult(default, CancelledError());
        }
        catch (OperationCanceledException)
        {
            return new BrokerPipeReadResult(default, MalformedError(), TimedOut: true);
        }
        catch (UnauthorizedAccessException)
        {
            return new BrokerPipeReadResult(default, PipeAccessDeniedError());
        }
        catch (IOException)
        {
            return Malformed();
        }
        catch (ObjectDisposedException)
        {
            return Malformed();
        }
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (body.Length is < BrokerPipeEndpoint.MinimumBodyLength
            or > BrokerPipeEndpoint.MaximumBodyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(body));
        }

        byte[] prefix = new byte[BrokerPipeEndpoint.LengthPrefixSize];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)body.Length));
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static BrokerError MalformedError() => new(
        BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE,
        "The request message is malformed.",
        false,
        null);

    internal static BrokerError PipeAccessDeniedError() => new(
        BrokerErrorCodes.FSL_E_PIPE_ACCESS_DENIED,
        "Access to the broker pipe was denied.",
        false,
        null);

    internal static BrokerError CancelledError() => new(
        BrokerErrorCodes.FSL_E_OPERATION_CANCELLED,
        "The operation was cancelled.",
        false,
        null);

    private static BrokerPipeReadResult Malformed() =>
        new(default, MalformedError());

    private static async ValueTask<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> body) =>
        body.Length >= 3
        && body[0] == 0xef
        && body[1] == 0xbb
        && body[2] == 0xbf;

    private static bool IsStrictUtf8(ReadOnlySpan<byte> body)
    {
        try
        {
            StrictUtf8.GetCharCount(body);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasAvailableData(Stream stream)
    {
        if (stream.CanSeek)
        {
            return stream.Position != stream.Length;
        }

        if (stream is not PipeStream pipeStream || !pipeStream.IsConnected)
        {
            return false;
        }

        if (!PeekNamedPipe(
                pipeStream.SafePipeHandle,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                out uint availableBytes,
                IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is ErrorBrokenPipe or ErrorNoData)
            {
                return false;
            }

            throw new IOException("The pipe input could not be inspected.", new Win32Exception(error));
        }

        return availableBytes != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(
        SafePipeHandle pipeHandle,
        IntPtr buffer,
        uint bufferSize,
        IntPtr bytesRead,
        out uint totalBytesAvailable,
        IntPtr bytesLeftThisMessage);
}
