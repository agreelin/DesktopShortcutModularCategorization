using System.Buffers.Binary;
using FolderSessionLock.App.BrokerClient;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.App.Tests.BrokerClient;

public sealed class ElevationBrokerClientTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 1, 2, 3, TimeSpan.Zero);
    private static readonly Guid RequestId =
        Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid ConnectionId =
        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    [Fact]
    public async Task ExecuteAsync_ReadinessFailureStopsBeforeIdentityPathAndLaunch()
    {
        var identity = new IdentityProvider();
        var path = new PathResolver();
        var launcher = new Launcher();
        var client = new ElevationBrokerClient(
            new ReadinessReader(BlockedReadiness()),
            identity,
            path,
            new AuthenticodeVerifier(),
            launcher,
            new BrokerConnectionRace(new NeverUsedConnector()),
            () => Now);

        BrokerClientResult result = await client.ExecuteAsync(Request(), nint.Zero, default);

        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_BLOCKING, result.Error!.Code);
        Assert.Equal(
            "Folder restrictions cannot be created until recovery is complete.",
            result.Error.Message);
        Assert.True(result.Error.Retryable);
        Assert.Equal(0, identity.Calls);
        Assert.Equal(0, path.Calls);
        Assert.Equal(0, launcher.Calls);
    }

    [Theory]
    [InlineData(
        "FSL_E_ACCOUNT_SID_MISMATCH",
        "The elevated broker account does not match the requesting account.",
        "FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED",
        "Cross-account elevation is not supported.")]
    [InlineData(
        "FSL_E_LOGON_SID_MISMATCH",
        "The broker and client do not belong to the same Windows logon session.",
        "FSL_E_LOGON_SID_MISMATCH",
        "The broker and client do not belong to the same Windows logon session.")]
    [InlineData(
        "FSL_E_SESSION_MISMATCH",
        "The broker and client do not belong to the same Windows session.",
        "FSL_E_SESSION_MISMATCH",
        "The broker and client do not belong to the same Windows session.")]
    public async Task ExecuteAsync_ConvertsOnlyConnectedAccountMismatch(
        string sourceCode,
        string sourceMessage,
        string expectedCode,
        string expectedMessage)
    {
        BrokerError source = new(sourceCode, sourceMessage, false, null);
        var server = BrokerServerHello.Failed(
            RequestId,
            BrokerCommand.ValidatePath,
            Now,
            source);
        var stream = new ScriptedDuplexStream(Frame(server));
        ElevationBrokerClient client = Client(stream, new BrokerProcess());

        BrokerClientResult result = await client.ExecuteAsync(Request(), nint.Zero, default);

        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(expectedMessage, result.Error.Message);
        Assert.False(result.Error.Retryable);
        Assert.Null(result.Error.Field);
    }

    [Fact]
    public async Task ExecuteAsync_UsesAcceptedBindingAndReturnsTheValidResponse()
    {
        string serverNonce = BrokerHandshakeBinding.CreateNonce();
        BrokerServerHello server = BrokerServerHello.Succeeded(
            RequestId,
            BrokerCommand.ValidatePath,
            Now,
            ConnectionId,
            serverNonce);
        BrokerResponseEnvelope response = BrokerResponseEnvelope.Succeeded(
            RequestId,
            BrokerCommand.ValidatePath,
            Now.AddSeconds(1),
            new ValidatePathResult(
                @"C:\Data\Locked",
                @"C:\",
                "0123456789abcdef",
                "1",
                "2",
                "NTFS",
                "Fixed",
                false,
                true));
        var commandResponse = new BrokerCommandResponse(
            BrokerFrameType.CommandResponse,
            BrokerProtocolConstants.HandshakeVersion,
            BrokerProtocolConstants.ProtocolVersion,
            RequestId,
            BrokerCommand.ValidatePath,
            ConnectionId,
            response);
        var stream = new ScriptedDuplexStream(Frame(server, commandResponse));
        var process = new BrokerProcess();
        ElevationBrokerClient client = Client(stream, process);

        BrokerClientResult result = await client.ExecuteAsync(Request(), nint.Zero, default);

        Assert.True(result.IsSuccess);
        Assert.True(result.Response!.Success);
        Assert.Equal(RequestId, result.Response.RequestId);
        Assert.Equal(BrokerCommand.ValidatePath.ToString(), result.Response.Command);
        Assert.Equal(0, process.TerminateCalls);

        IReadOnlyList<object> writes = ParseWrittenFrames(stream.WrittenBytes);
        var hello = Assert.IsType<BrokerClientHello>(writes[0]);
        Assert.Equal(RequestId, hello.RequestId);
        Assert.Equal(BrokerCommand.ValidatePath, hello.Command);
        Assert.Equal(42u, hello.ClaimedClientProcessId);
        Assert.Equal(7u, hello.ClientSessionId);
        var command = Assert.IsType<BrokerCommandRequest>(writes[1]);
        Assert.Equal(ConnectionId, command.ConnectionId);
        Assert.True(BrokerHandshakeBinding.VerifyProof(
            command.BindingProof,
            RequestId,
            BrokerCommand.ValidatePath,
            ConnectionId,
            hello.ClientNonce,
            serverNonce,
            7));
    }

    [Fact]
    public async Task ExecuteAsync_ElevationCancellationRemainsRetryable()
    {
        var launcher = new Launcher(Result<IBrokerProcessHandle>.Failure(new Error(
            BrokerErrorCodes.FSL_E_ELEVATION_CANCELLED,
            "The elevation request was cancelled.",
            ErrorCategory.RecoverableError)));
        var client = new ElevationBrokerClient(
            new ReadinessReader(ReadyReadiness()),
            new IdentityProvider(),
            new PathResolver(),
            new AuthenticodeVerifier(),
            launcher,
            new BrokerConnectionRace(new NeverUsedConnector()),
            () => Now);

        BrokerClientResult result = await client.ExecuteAsync(Request(), nint.Zero, default);

        Assert.Equal(BrokerErrorCodes.FSL_E_ELEVATION_CANCELLED, result.Error!.Code);
        Assert.Equal("The elevation request was cancelled.", result.Error.Message);
        Assert.True(result.Error.Retryable);
        Assert.Null(result.Error.Field);
    }

    [Fact]
    public async Task ExecuteAsync_SignatureFailureStopsBeforeLaunchAndConnection()
    {
        var path = new PathResolver();
        var signature = new AuthenticodeVerifier(Result.Failure(PathError()));
        var launcher = new Launcher();
        var client = new ElevationBrokerClient(
            new ReadinessReader(ReadyReadiness()),
            new IdentityProvider(),
            path,
            signature,
            launcher,
            new BrokerConnectionRace(new NeverUsedConnector()),
            () => Now);

        BrokerClientResult result = await client.ExecuteAsync(Request(), nint.Zero, default);

        AssertPathFailure(result);
        Assert.Equal(1, path.Calls);
        Assert.Equal(1, signature.Calls);
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_IdentityChangeAfterSignatureStopsBeforeLaunch()
    {
        var path = new PathResolver(
            new BrokerFileIdentity(1, 2, 3),
            new BrokerFileIdentity(1, 2, 4));
        var signature = new AuthenticodeVerifier();
        var launcher = new Launcher();
        var client = new ElevationBrokerClient(
            new ReadinessReader(ReadyReadiness()),
            new IdentityProvider(),
            path,
            signature,
            launcher,
            new BrokerConnectionRace(new NeverUsedConnector()),
            () => Now);

        BrokerClientResult result = await client.ExecuteAsync(Request(), nint.Zero, default);

        AssertPathFailure(result);
        Assert.Equal(2, path.Calls);
        Assert.Equal(1, signature.Calls);
        Assert.Equal(0, launcher.Calls);
    }

    private static ElevationBrokerClient Client(
        Stream stream,
        BrokerProcess process) => new(
            new ReadinessReader(ReadyReadiness()),
            new IdentityProvider(),
            new PathResolver(),
            new AuthenticodeVerifier(),
            new Launcher(Result<IBrokerProcessHandle>.Success(process)),
            new BrokerConnectionRace(new ImmediateConnector(stream)),
            () => Now);

    private static BrokerRequestEnvelope Request() => new(
        BrokerProtocolConstants.ProtocolVersion,
        RequestId,
        BrokerCommand.ValidatePath,
        7,
        Now,
        new ValidatePathRequest(@"C:\Data\Locked"));

    private static RecoveryReadinessSnapshot ReadyReadiness() => new(
        RecoveryReadinessPolicy.SchemaVersion,
        RecoveryReadinessPolicy.ServiceName,
        Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"),
        1,
        RecoveryReadinessState.Ready,
        false,
        Now.AddSeconds(-2),
        Now.AddSeconds(-1),
        Now.AddSeconds(-1),
        Now.AddSeconds(29),
        0,
        null);

    private static RecoveryReadinessSnapshot BlockedReadiness() =>
        ReadyReadiness() with
        {
            State = RecoveryReadinessState.RecoveryBlocked,
            RecoveryBlocking = true,
            RemainingRecordCount = 1,
            PrimaryErrorCode = BrokerErrorCodes.FSL_E_RECOVERY_BLOCKING,
        };

    private static byte[] Frame(params object[] frames)
    {
        using var stream = new MemoryStream();
        foreach (object frame in frames)
        {
            byte[] body = BrokerHandshakeProtocolJson.SerializeFrame(frame);
            byte[] prefix = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)body.Length));
            stream.Write(prefix);
            stream.Write(body);
        }

        return stream.ToArray();
    }

    private static IReadOnlyList<object> ParseWrittenFrames(byte[] bytes)
    {
        var frames = new List<object>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
            offset += sizeof(uint);
            BrokerHandshakeFrameParseResult parsed =
                BrokerHandshakeProtocolJson.DeserializeFrame(
                    bytes.AsMemory(offset, checked((int)length)),
                    Now,
                    LockDurationPolicy.CreateProduction());
            Assert.True(parsed.IsSuccess, parsed.Error?.Code);
            frames.Add(parsed.Frame!);
            offset += checked((int)length);
        }

        return frames;
    }

    private sealed class ReadinessReader(RecoveryReadinessSnapshot snapshot)
        : IRecoveryReadinessReader
    {
        public ValueTask<RecoveryReadinessSnapshot> ReadAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(snapshot);
    }

    private sealed class IdentityProvider : IInitiatingClientIdentityProvider
    {
        internal int Calls { get; private set; }

        public Result<InitiatingClientIdentity> Capture()
        {
            Calls++;
            return Result<InitiatingClientIdentity>.Success(new(
                42,
                123456789,
                "S-1-5-21-1",
                "S-1-5-5-1-2",
                7));
        }
    }

    private static Error PathError() => new(
        BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED,
        "The elevated broker installation could not be verified.",
        ErrorCategory.UnrecoverableError);

    private static void AssertPathFailure(BrokerClientResult result)
    {
        Assert.Equal(BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED, result.Error!.Code);
        Assert.Equal(
            "The elevated broker installation could not be verified.",
            result.Error.Message);
        Assert.False(result.Error.Retryable);
        Assert.Null(result.Error.Field);
    }

    private sealed class PathResolver(params BrokerFileIdentity[] identities)
        : IBrokerPathResolver
    {
        internal int Calls { get; private set; }

        public Result<ResolvedBrokerPath> Resolve()
        {
            Calls++;
            string directory = Path.GetFullPath(@"C:\Program Files\FolderSessionLock");
            BrokerFileIdentity identity = identities.Length == 0
                ? new BrokerFileIdentity(1, 2, 3)
                : identities[Math.Min(Calls - 1, identities.Length - 1)];
            return Result<ResolvedBrokerPath>.Success(new(
                directory,
                Path.Combine(directory, "FolderSessionLock.Broker.exe"),
                identity));
        }
    }

    private sealed class AuthenticodeVerifier(Result? result = null)
        : IBrokerAuthenticodeVerifier
    {
        private readonly Result _result = result ?? Result.Success();

        internal int Calls { get; private set; }

        public Result Verify(string brokerPath)
        {
            Calls++;
            return _result;
        }
    }

    private sealed class Launcher(Result<IBrokerProcessHandle>? result = null)
        : IConsentElevationLauncher
    {
        private readonly Result<IBrokerProcessHandle> _result = result
            ?? Result<IBrokerProcessHandle>.Success(new BrokerProcess());

        internal int Calls { get; private set; }

        public ValueTask<Result<IBrokerProcessHandle>> LaunchAsync(
            ConsentElevationLaunchRequest request)
        {
            Calls++;
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class ImmediateConnector(Stream stream) : IBrokerPipeConnector
    {
        public ValueTask<Result<Stream>> ConnectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<Stream>.Success(stream));
    }

    private sealed class NeverUsedConnector : IBrokerPipeConnector
    {
        public ValueTask<Result<Stream>> ConnectAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed class BrokerProcess : IBrokerProcessHandle
    {
        internal int TerminateCalls { get; private set; }

        public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public Result<int> GetExitCode() => Result<int>.Success(0);

        public Result Terminate(uint exitCode)
        {
            TerminateCalls++;
            return Result.Success();
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedDuplexStream(byte[] readBytes) : Stream
    {
        private readonly MemoryStream _read = new(readBytes, writable: false);
        private readonly MemoryStream _write = new();

        internal byte[] WrittenBytes => _write.ToArray();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            _read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            _write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _write.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
