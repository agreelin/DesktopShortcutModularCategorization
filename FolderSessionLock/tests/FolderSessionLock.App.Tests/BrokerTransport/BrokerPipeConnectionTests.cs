using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Broker.Recovery.Tests;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Broker.Transport;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Transport.Tests;

public sealed class BrokerPipeConnectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 16, 30, 0, TimeSpan.Zero);
    private static readonly Guid RequestId = Guid.ParseExact("11111111-2222-3333-4444-555555555555", "D");
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromMinutes(1), TimeSpan.FromHours(8)).Value;

    [Fact]
    public async Task ProcessAsync_CompletesFourFrameHandshakeAndReplaySuccess()
    {
        await using TestContext context = await TestContext.Create();
        Task<BrokerPipeConnectionResult> server = context.Start();
        BrokerClientHello hello = context.CreateHello();
        await WriteAsync(context.Client, hello);
        BrokerServerHello serverHello = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        Assert.True(serverHello.Success);
        await WriteAsync(context.Client, context.CreateCommandRequest(hello, serverHello));

        BrokerCommandResponse response = Assert.IsType<BrokerCommandResponse>((await ReadAsync(context.Client)).Frame);
        BrokerPipeConnectionResult result = await server;

        Assert.True(response.Response.Success);
        Assert.True(result.ResponseWritten);
        Assert.Null(result.Error);
        Assert.Equal(1, context.ProcessCalls);
        Assert.Equal("Succeeded", context.ReplayState());
        Assert.Null(context.ReplayProperty("terminalCode"));
        Assert.NotNull(context.ReplayProperty("retentionExpiresUtc"));
    }

    [Theory]
    [InlineData(BrokerExecutionEffect.FailedWithoutSideEffects, "Failed", BrokerErrorCodes.FSL_E_INTERNAL, true)]
    [InlineData(BrokerExecutionEffect.RolledBack, "RolledBack", BrokerErrorCodes.FSL_E_INTERNAL, true)]
    [InlineData(
        BrokerExecutionEffect.RecoveryRequired,
        "RecoveryRequired",
        BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
        false)]
    public async Task ProcessAsync_ExecutionOutcomeMapsReplayTerminalState(
        BrokerExecutionEffect effect,
        string expectedState,
        string expectedTerminalCode,
        bool hasRetention)
    {
        await using TestContext context = await TestContext.Create();
        BrokerResponseEnvelope failedResponse = BrokerResponseEnvelope.Failed(
            RequestId,
            BrokerCommand.GetStatus,
            Now,
            BrokerError.Internal());
        BrokerExecutionOutcome outcome = effect switch
        {
            BrokerExecutionEffect.FailedWithoutSideEffects =>
                BrokerExecutionOutcome.FailedWithoutSideEffects(failedResponse),
            BrokerExecutionEffect.RolledBack => BrokerExecutionOutcome.RolledBack(failedResponse),
            BrokerExecutionEffect.RecoveryRequired => BrokerExecutionOutcome.RecoveryRequired(failedResponse),
            _ => throw new InvalidOperationException(),
        };
        Task<BrokerPipeConnectionResult> server = context.Start(executionOutcome: outcome);
        BrokerClientHello hello = context.CreateHello();
        await WriteAsync(context.Client, hello);
        BrokerServerHello serverHello = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        await WriteAsync(context.Client, context.CreateCommandRequest(hello, serverHello));

        BrokerCommandResponse response = Assert.IsType<BrokerCommandResponse>((await ReadAsync(context.Client)).Frame);
        await server;

        Assert.Equal(BrokerErrorCodes.FSL_E_INTERNAL, response.Response.Error!.Code);
        Assert.Equal(expectedState, context.ReplayState());
        Assert.Equal(expectedTerminalCode, context.ReplayProperty("terminalCode"));
        Assert.Equal(hasRetention, context.ReplayProperty("retentionExpiresUtc") is not null);
    }

    [Fact]
    public async Task ProcessAsync_RecoveryRecordDeleteFailureFlowsFromWindowsServiceToReplay()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        string recordsDirectory = Path.Combine(testRoot, "records");
        string targetPath = Path.Combine(testRoot, "target");
        Directory.CreateDirectory(targetPath);
        var clock = new FixedClock(Now);
        var recoveryRegistry = new RecoveryTaskRegistry();
        var filePlatform = new DeleteFailureRecoveryStorePlatform();
        var store = RecoveryTestData.CreateStore(
            recordsDirectory,
            filePlatform: filePlatform);
        var innerTransaction = new RecoveryRecordTransaction(store, recoveryRegistry, clock);
        var transaction = new RecordTrackingRecoveryTransaction(innerTransaction);
        var pathRelation = new WindowsFolderPathRelationService();
        var manager = new LockTaskManager(pathRelation);
        var folderLockService = new WindowsFolderLockService(
            new WindowsSessionIdentityProvider(),
            CreatePathValidator(testRoot),
            pathRelation,
            new DirectoryAclEditor(new RolledBackAddHook()),
            transaction);
        var coordinator = new LockTaskCoordinator(
            manager,
            folderLockService,
            clock,
            NullLogger<LockTaskCoordinator>.Instance);
        var processor = new BrokerCommandProcessor(
            CreatePathValidator(testRoot),
            manager,
            coordinator,
            folderLockService,
            recoveryRegistry,
            clock,
            DurationPolicy,
            FolderSessionLock.Broker.Recovery.Tests.RecoveryReadinessTests.ReadyGate());
        Guid taskId = Guid.ParseExact("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "D");
        BrokerExecutionOutcome? processorOutcome = null;

        try
        {
            await using TestContext context = await TestContext.Create();
            BrokerClientHello hello = context.CreateHello() with { Command = BrokerCommand.CreateLock };
            var innerRequest = new BrokerRequestEnvelope(
                1,
                RequestId,
                BrokerCommand.CreateLock,
                1,
                Now,
                new CreateLockRequest(taskId, targetPath, 60_000));
            Task<BrokerPipeConnectionResult> server = context.Start(
                processRequest: async (request, cancellationToken) =>
                {
                    processorOutcome = await processor.ProcessAsync(
                        request,
                        BrokerExecutionContext.OrdinaryUi,
                        cancellationToken);
                    return processorOutcome;
                });
            await WriteAsync(context.Client, hello);
            BrokerServerHello serverHello = Assert.IsType<BrokerServerHello>(
                (await ReadAsync(context.Client)).Frame);
            await WriteAsync(
                context.Client,
                context.CreateCommandRequest(hello, serverHello, innerRequest));

            BrokerCommandResponse response = Assert.IsType<BrokerCommandResponse>(
                (await ReadAsync(context.Client)).Frame);
            await server;

            Assert.Equal(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED,
                response.Response.Error!.Code);
            Assert.Equal(BrokerExecutionEffect.RecoveryRequired, processorOutcome!.Effect);
            Assert.Equal(
                LockTaskStatus.RecoveryRequired,
                manager.GetById(FolderLockTaskId.Create(taskId).Value).Value.Status);
            Assert.True(File.Exists(store.GetRecordPath(transaction.RecordId)));
            Assert.NotNull(recoveryRegistry.GetByRecordId(transaction.RecordId));
            Assert.Equal("RecoveryRequired", context.ReplayState());
            Assert.Equal(
                BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
                context.ReplayProperty("terminalCode"));
        }
        finally
        {
            filePlatform.DeleteFailureEnabled = false;
            if (transaction.RecordId != Guid.Empty)
            {
                Assert.True((await innerTransaction.DeleteAsync(
                    transaction.RecordId,
                    default)).IsSuccess);
            }

            string probe = Path.Combine(targetPath, "probe.txt");
            File.WriteAllText(probe, "probe");
            Assert.Equal("probe", File.ReadAllText(probe));
            File.Delete(probe);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_ClientDisconnectAfterServerHelloDoesNotExecuteOrReapplyCommand()
    {
        await using TestContext context = await TestContext.Create();
        Task<BrokerPipeConnectionResult> server = context.Start();
        await WriteAsync(context.Client, context.CreateHello());
        _ = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);

        await context.Client.DisposeAsync();
        BrokerPipeConnectionResult result = await server;

        Assert.False(result.ResponseWritten);
        Assert.Equal(0, context.ProcessCalls);
        Assert.Equal("Failed", context.ReplayState());
    }

    [Fact]
    public async Task ProcessAsync_CreateLockResponseWriteFailureLeavesActiveAppliedUntilExplicitCleanup()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        string recordsDirectory = Path.Combine(testRoot, "records");
        string targetPath = Path.Combine(testRoot, "target");
        Directory.CreateDirectory(targetPath);
        var clock = new FixedClock(Now);
        var recoveryRegistry = new RecoveryTaskRegistry();
        var store = RecoveryTestData.CreateStore(recordsDirectory);
        var transaction = new RecoveryRecordTransaction(store, recoveryRegistry, clock);
        var pathRelation = new WindowsFolderPathRelationService();
        var manager = new LockTaskManager(pathRelation);
        var folderLockService = new WindowsFolderLockService(
            new WindowsSessionIdentityProvider(),
            CreatePathValidator(testRoot),
            pathRelation,
            new DirectoryAclEditor(),
            transaction);
        var coordinator = new LockTaskCoordinator(
            manager,
            folderLockService,
            clock,
            NullLogger<LockTaskCoordinator>.Instance);
        var processor = new BrokerCommandProcessor(
            CreatePathValidator(testRoot),
            manager,
            coordinator,
            folderLockService,
            recoveryRegistry,
            clock,
            DurationPolicy,
            FolderSessionLock.Broker.Recovery.Tests.RecoveryReadinessTests.ReadyGate());
        Guid taskId = Guid.ParseExact("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "D");
        Guid recordId = Guid.Empty;

        try
        {
            await using TestContext context = await TestContext.Create();
            BrokerClientHello hello = context.CreateHello() with { Command = BrokerCommand.CreateLock };
            var innerRequest = new BrokerRequestEnvelope(
                1,
                RequestId,
                BrokerCommand.CreateLock,
                1,
                Now,
                new CreateLockRequest(taskId, targetPath, 60_000));
            var failingStream = new FailingWriteStream(context.Server, failOnWriteCall: 3);
            Task<BrokerPipeConnectionResult> server = context.Start(
                stream: failingStream,
                processRequest: (request, cancellationToken) => processor.ProcessAsync(
                    request,
                    BrokerExecutionContext.OrdinaryUi,
                    cancellationToken));
            await WriteAsync(context.Client, hello);
            BrokerServerHello serverHello = Assert.IsType<BrokerServerHello>(
                (await ReadAsync(context.Client)).Frame);
            await WriteAsync(
                context.Client,
                context.CreateCommandRequest(hello, serverHello, innerRequest));

            BrokerPipeConnectionResult connectionResult = await server;
            FolderLockTask task = manager.GetById(FolderLockTaskId.Create(taskId).Value).Value;
            RecoveryRecord record = recoveryRegistry.GetByTaskId(taskId)!;
            recordId = record.RecordId;

            Assert.False(connectionResult.ResponseWritten);
            Assert.Equal(BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE, connectionResult.Error!.Code);
            Assert.Equal(LockTaskStatus.Active, task.Status);
            Assert.Equal(RecoveryRecordState.Applied, record.State);
            Assert.True(File.Exists(store.GetRecordPath(recordId)));
            Assert.NotNull(folderLockService.GetActiveRecord(taskId));
            Assert.Equal("Succeeded", context.ReplayState());

            Result<int> cleanup = await coordinator.ProcessAdministrativeCleanupAsync();
            Assert.True(cleanup.IsSuccess, cleanup.Error?.Code);
            Assert.Equal(1, cleanup.Value);
            Assert.Equal(
                LockTaskStatus.Completed,
                manager.GetById(FolderLockTaskId.Create(taskId).Value).Value.Status);
            Assert.Null(recoveryRegistry.GetByTaskId(taskId));
            Assert.False(File.Exists(store.GetRecordPath(recordId)));
        }
        finally
        {
            if (folderLockService.GetActiveRecord(taskId) is not null)
            {
                Assert.True((await folderLockService.RemoveLockAsync(
                    taskId,
                    LockRemovalIntent.TestCleanup)).IsSuccess);
            }

            if (recordId != Guid.Empty && recoveryRegistry.GetByRecordId(recordId) is not null)
            {
                Assert.True((await transaction.DeleteAsync(recordId, default)).IsSuccess);
            }

            string probe = Path.Combine(targetPath, "probe.txt");
            File.WriteAllText(probe, "probe");
            Assert.Equal("probe", File.ReadAllText(probe));
            File.Delete(probe);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_FirstCommandRequestReturnsHandshakeRequiredWithoutReplay()
    {
        await using TestContext context = await TestContext.Create();
        Task<BrokerPipeConnectionResult> server = context.Start();
        BrokerClientHello hello = context.CreateHello();
        var request = new BrokerCommandRequest(
            BrokerFrameType.CommandRequest,
            1,
            1,
            hello.RequestId,
            hello.Command,
            Guid.ParseExact("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "D"),
            BrokerHandshakeBinding.CreateNonce(),
            context.CreateInnerRequest());

        await WriteAsync(context.Client, request);
        BrokerServerHello response = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        await server;

        Assert.False(response.Success);
        Assert.Equal(BrokerErrorCodes.FSL_E_HANDSHAKE_REQUIRED, response.Error!.Code);
        Assert.True(response.Error.Retryable);
        Assert.Equal("frameType", response.Error.Field);
        Assert.Empty(Directory.EnumerateFiles(context.ReplayRoot));
    }

    [Theory]
    [InlineData(
        BrokerErrorCodes.FSL_E_CLIENT_PROCESS_MISMATCH,
        "The connected client process does not match the handshake.",
        "claimedClientProcessId")]
    [InlineData(
        BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE,
        "The client identity could not be verified.",
        null)]
    [InlineData(
        BrokerErrorCodes.FSL_E_ACCOUNT_SID_MISMATCH,
        "The elevated broker account does not match the requesting account.",
        null)]
    [InlineData(
        BrokerErrorCodes.FSL_E_LOGON_SID_MISMATCH,
        "The broker and client do not belong to the same Windows logon session.",
        null)]
    [InlineData(
        BrokerErrorCodes.FSL_E_SESSION_MISMATCH,
        "The broker and client do not belong to the same Windows session.",
        "clientSessionId")]
    public async Task ProcessAsync_IdentityFailureCreatesNoReplayFile(
        string code,
        string message,
        string? field)
    {
        BrokerError identityError = new(
            code,
            message,
            false,
            field);
        await using TestContext context = await TestContext.Create(identityError);
        Task<BrokerPipeConnectionResult> server = context.Start();

        await WriteAsync(context.Client, context.CreateHello());
        BrokerServerHello response = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        await server;

        Assert.Equal(identityError, response.Error);
        Assert.Empty(Directory.EnumerateFiles(context.ReplayRoot));
        Assert.Equal(0, context.ProcessCalls);
    }

    [Fact]
    public async Task ProcessAsync_UnauthorizedCommandCreatesNoReplayFile()
    {
        await using TestContext context = await TestContext.Create();
        Task<BrokerPipeConnectionResult> server = context.Start();
        BrokerClientHello hello = context.CreateHello() with { Command = BrokerCommand.RemoveLock };

        await WriteAsync(context.Client, hello);
        BrokerServerHello response = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        await server;

        Assert.Equal(BrokerErrorCodes.FSL_E_UNAUTHORIZED_CALLER, response.Error!.Code);
        Assert.Empty(Directory.EnumerateFiles(context.ReplayRoot));
        Assert.Equal(0, context.ProcessCalls);
    }

    [Fact]
    public async Task ProcessAsync_CliBindingFailureCreatesNoReplayFile()
    {
        await using TestContext context = await TestContext.Create();
        Task<BrokerPipeConnectionResult> server = context.Start();
        BrokerClientHello hello = context.CreateHello() with { RequestId = Guid.NewGuid() };

        await WriteAsync(context.Client, hello);
        BrokerServerHello response = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        await server;

        Assert.Equal(BrokerErrorCodes.FSL_E_REQUEST_BINDING_MISMATCH, response.Error!.Code);
        Assert.Empty(Directory.EnumerateFiles(context.ReplayRoot));
    }

    [Fact]
    public async Task ProcessAsync_ActiveReplayReturnsRequestInProgressWithoutChangingOwner()
    {
        await using TestContext context = await TestContext.Create();
        ReplayAcquireResult owner = await context.ReplayRegistry.AcquireAsync(
            context.AuthenticatedClient,
            RequestId,
            BrokerCommand.GetStatus);
        Assert.True(owner.IsSuccess);
        string before = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(context.ReplayRoot)));
        Task<BrokerPipeConnectionResult> server = context.Start();

        await WriteAsync(context.Client, context.CreateHello());
        BrokerServerHello response = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        await server;
        string after = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(context.ReplayRoot)));

        Assert.Equal(BrokerErrorCodes.FSL_E_REQUEST_IN_PROGRESS, response.Error!.Code);
        Assert.True(response.Error.Retryable);
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(ReplayState.Succeeded)]
    [InlineData(ReplayState.Failed)]
    [InlineData(ReplayState.RolledBack)]
    [InlineData(ReplayState.Abandoned)]
    [InlineData(ReplayState.RecoveryRequired)]
    public async Task ProcessAsync_TerminalReplayReturnsReplayDetectedWithoutChangingRecord(
        ReplayState terminalState)
    {
        await using TestContext context = await TestContext.Create();
        ReplayAcquireResult owner = await context.ReplayRegistry.AcquireAsync(
            context.AuthenticatedClient,
            RequestId,
            BrokerCommand.GetStatus);
        string? terminalCode = terminalState switch
        {
            ReplayState.Succeeded => null,
            ReplayState.Abandoned => BrokerErrorCodes.FSL_E_HANDSHAKE_EXPIRED,
            ReplayState.RecoveryRequired => BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
            _ => BrokerErrorCodes.FSL_E_INTERNAL,
        };
        Assert.Null(await context.ReplayRegistry.CompleteAsync(
            owner.Lease!,
            terminalState,
            terminalCode));
        string before = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(context.ReplayRoot)));
        Task<BrokerPipeConnectionResult> server = context.Start();

        await WriteAsync(context.Client, context.CreateHello());
        BrokerServerHello response = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        await server;
        string after = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(context.ReplayRoot)));

        Assert.Equal(BrokerErrorCodes.FSL_E_REPLAY_DETECTED, response.Error!.Code);
        Assert.False(response.Error.Retryable);
        Assert.Equal("requestId", response.Error.Field);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ProcessAsync_BindingMismatchUsesAcceptedIdentifiersAndFailedReplay()
    {
        await using TestContext context = await TestContext.Create();
        Task<BrokerPipeConnectionResult> server = context.Start();
        BrokerClientHello hello = context.CreateHello();
        await WriteAsync(context.Client, hello);
        BrokerServerHello serverHello = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        BrokerCommandRequest request = context.CreateCommandRequest(hello, serverHello) with
        {
            BindingProof = BrokerHandshakeBinding.CreateNonce(),
        };

        await WriteAsync(context.Client, request);
        BrokerCommandResponse response = Assert.IsType<BrokerCommandResponse>((await ReadAsync(context.Client)).Frame);
        await server;

        Assert.Equal(hello.RequestId, response.RequestId);
        Assert.Equal(hello.Command, response.Command);
        Assert.Equal(serverHello.Result!.ConnectionId, response.ConnectionId);
        Assert.Equal(BrokerErrorCodes.FSL_E_REQUEST_BINDING_MISMATCH, response.Response.Error!.Code);
        Assert.Equal("Failed", context.ReplayState());
    }

    [Fact]
    public async Task ProcessAsync_RepeatedClientHelloReturnsProtocolSequenceInvalid()
    {
        await using TestContext context = await TestContext.Create();
        Task<BrokerPipeConnectionResult> server = context.Start();
        BrokerClientHello hello = context.CreateHello();
        await WriteAsync(context.Client, hello);
        BrokerServerHello serverHello = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);

        await WriteAsync(context.Client, hello);
        BrokerCommandResponse response = Assert.IsType<BrokerCommandResponse>((await ReadAsync(context.Client)).Frame);
        await server;

        Assert.Equal(BrokerErrorCodes.FSL_E_PROTOCOL_SEQUENCE_INVALID, response.Response.Error!.Code);
        Assert.Equal(serverHello.Result!.ConnectionId, response.ConnectionId);
        Assert.Equal("Failed", context.ReplayState());
    }

    [Fact]
    public async Task ProcessAsync_ExpiredHandshakeReturnsCommandFailureAndAbandonsReplay()
    {
        await using TestContext context = await TestContext.Create();
        Task<BrokerPipeConnectionResult> server = context.Start(new ExpiringHandshakeClock());
        BrokerClientHello hello = context.CreateHello();
        await WriteAsync(context.Client, hello);
        BrokerServerHello serverHello = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);

        BrokerCommandResponse response = Assert.IsType<BrokerCommandResponse>((await ReadAsync(context.Client)).Frame);
        BrokerPipeConnectionResult result = await server;

        Assert.Equal(serverHello.Result!.ConnectionId, response.ConnectionId);
        Assert.Equal(BrokerErrorCodes.FSL_E_HANDSHAKE_EXPIRED, response.Response.Error!.Code);
        Assert.True(response.Response.Error.Retryable);
        Assert.Null(response.Response.Error.Field);
        Assert.True(result.ResponseWritten);
        Assert.Equal("Abandoned", context.ReplayState());
        Assert.Equal(BrokerErrorCodes.FSL_E_HANDSHAKE_EXPIRED, context.ReplayProperty("terminalCode"));
    }

    public static IEnumerable<object[]> MalformedCommandFrames()
    {
        yield return [LengthPrefix(0)];
        yield return [LengthPrefix(BrokerPipeEndpoint.MaximumBodyLength + 1)];
        yield return [BrokerPipeFrameCodecTests.Frame([0xef, 0xbb, 0xbf, (byte)'{', (byte)'}'])];
        yield return [BrokerPipeFrameCodecTests.Frame([0xc3, 0x28])];
    }

    [Theory]
    [MemberData(nameof(MalformedCommandFrames))]
    public async Task ProcessAsync_MalformedCommandFrameReturnsMalformedAndFailsReplay(byte[] frame) =>
        await AssertMalformedCommandFrame(frame);

    [Fact]
    public async Task ProcessAsync_TruncatedCommandFrameReturnsMalformedAndFailsReplay()
    {
        byte[] frame = [.. LengthPrefix(5), (byte)'{', (byte)'}'];
        await AssertMalformedCommandFrame(
            frame,
            (server, firstFrameLength) => new LimitedDuplexStream(
                server,
                firstFrameLength + frame.Length,
                null));
    }

    [Fact]
    public async Task ProcessAsync_CommandReadIoFailureReturnsMalformedAndFailsReplay() =>
        await AssertMalformedCommandFrame(
            [],
            (server, firstFrameLength) => new LimitedDuplexStream(
                server,
                firstFrameLength,
                new IOException("Injected command frame read failure.")));

    private static async Task WriteAsync(Stream stream, object frame) =>
        await BrokerPipeFrameCodec.WriteAsync(stream, BrokerHandshakeProtocolJson.SerializeFrame(frame));

    private static byte[] LengthPrefix(int declaredLength)
    {
        byte[] prefix = new byte[BrokerPipeEndpoint.LengthPrefixSize];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)declaredLength));
        return prefix;
    }

    private static async Task AssertMalformedCommandFrame(
        byte[] secondFrame,
        Func<Stream, int, Stream>? wrapServer = null)
    {
        await using TestContext context = await TestContext.Create();
        BrokerClientHello hello = context.CreateHello();
        int firstFrameLength = BrokerPipeEndpoint.LengthPrefixSize
            + BrokerHandshakeProtocolJson.SerializeFrame(hello).Length;
        Stream serverStream = wrapServer?.Invoke(context.Server, firstFrameLength) ?? context.Server;
        Task<BrokerPipeConnectionResult> server = context.Start(stream: serverStream);
        await WriteAsync(context.Client, hello);
        BrokerServerHello serverHello = Assert.IsType<BrokerServerHello>((await ReadAsync(context.Client)).Frame);
        await context.Client.WriteAsync(secondFrame);
        await context.Client.FlushAsync();

        BrokerCommandResponse response = Assert.IsType<BrokerCommandResponse>((await ReadAsync(context.Client)).Frame);
        await server;

        Assert.Equal(serverHello.Result!.ConnectionId, response.ConnectionId);
        Assert.Equal(BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE, response.Response.Error!.Code);
        Assert.Equal("Failed", context.ReplayState());
        Assert.Equal(BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE, context.ReplayProperty("terminalCode"));
    }

    private static async Task<BrokerHandshakeFrameParseResult> ReadAsync(Stream stream)
    {
        BrokerPipeReadResult frame = await BrokerPipeFrameCodec.ReadAsync(stream, TimeSpan.FromSeconds(2));
        Assert.True(frame.IsSuccess, frame.Error?.Code);
        return BrokerHandshakeProtocolJson.DeserializeFrame(frame.Body, Now, DurationPolicy);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly FixedAuthenticator _authenticator;

        private TestContext(
            NamedPipeServerStream server,
            NamedPipeClientStream client,
            string replayRoot,
            FileReplayRegistry replayRegistry,
            FixedAuthenticator authenticator)
        {
            _server = server;
            Client = client;
            ReplayRoot = replayRoot;
            ReplayRegistry = replayRegistry;
            _authenticator = authenticator;
        }

        internal NamedPipeClientStream Client { get; }

        internal NamedPipeServerStream Server => _server;

        internal string ReplayRoot { get; }

        internal FileReplayRegistry ReplayRegistry { get; }

        internal BrokerAuthenticatedClient AuthenticatedClient => _authenticator.Client!;

        internal int ProcessCalls { get; private set; }

        internal static async Task<TestContext> Create(BrokerError? authenticationError = null)
        {
            string pipeName = $"FolderSessionLock.Tests.{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task waiting = server.WaitForConnectionAsync();
            await client.ConnectAsync(2_000);
            await waiting;
            string replayRoot = Path.Combine(
                Path.GetTempPath(),
                "FolderSessionLock.Tests",
                Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(replayRoot);
            var clock = new FixedClock(Now);
            var replay = new FileReplayRegistry(
                replayRoot,
                $"Local\\FolderSessionLock.Tests.{Guid.NewGuid():N}",
                clock,
                new NoneEvidence());
            SessionIdentity identity = new("S-1-5-21-100-200-300-400", "S-1-5-5-100-200", 1);
            var authenticated = new BrokerAuthenticatedClient(123, Now.AddMinutes(-1), identity, identity);
            return new TestContext(
                server,
                client,
                replayRoot,
                replay,
                new FixedAuthenticator(authenticationError is null ? authenticated : null, authenticationError));
        }

        internal Task<BrokerPipeConnectionResult> Start(
            IClock? clock = null,
            BrokerExecutionOutcome? executionOutcome = null,
            Stream? stream = null,
            Func<BrokerRequestEnvelope, CancellationToken, ValueTask<BrokerExecutionOutcome>>?
                processRequest = null) => BrokerPipeConnection.ProcessAsync(
            stream ?? _server,
            new BrokerConsentOptions(
                BrokerPipeEndpoint.PipeName,
                1,
                RequestId,
                1234,
                133970112000000000),
            DurationPolicy,
            clock ?? new FixedClock(Now),
            _authenticator,
            ReplayRegistry,
            (request, _) =>
            {
                ProcessCalls++;
                return processRequest is not null
                    ? processRequest(request, _)
                    : ValueTask.FromResult(executionOutcome ?? BrokerExecutionOutcome.Succeeded(
                    BrokerResponseEnvelope.Succeeded(
                        request.RequestId,
                        request.Command,
                        Now,
                        new GetStatusResult(GetStatusQueryType.CurrentSession, []))));
            }).AsTask();

        internal BrokerClientHello CreateHello() => new(
            BrokerFrameType.ClientHello,
            1,
            1,
            RequestId,
            BrokerCommand.GetStatus,
            123,
            1,
            BrokerHandshakeBinding.CreateNonce(),
            Now);

        internal BrokerRequestEnvelope CreateInnerRequest() => new(
            1,
            RequestId,
            BrokerCommand.GetStatus,
            1,
            Now,
            new GetStatusRequest(GetStatusQueryType.CurrentSession, null));

        internal BrokerCommandRequest CreateCommandRequest(
            BrokerClientHello hello,
            BrokerServerHello serverHello,
            BrokerRequestEnvelope? innerRequest = null)
        {
            BrokerServerHelloResult result = serverHello.Result!;
            return new BrokerCommandRequest(
                BrokerFrameType.CommandRequest,
                1,
                1,
                hello.RequestId,
                hello.Command,
                result.ConnectionId,
                BrokerHandshakeBinding.CreateProof(
                    hello.RequestId,
                    hello.Command,
                    result.ConnectionId,
                    hello.ClientNonce,
                    result.ServerNonce,
                    hello.ClientSessionId),
                innerRequest ?? CreateInnerRequest());
        }

        internal string ReplayState()
            => ReplayProperty("state")!;

        internal string? ReplayProperty(string name)
        {
            string file = Assert.Single(Directory.EnumerateFiles(ReplayRoot, "*.fsrr"));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
            JsonElement value = document.RootElement.GetProperty(name);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _server.DisposeAsync();
            if (Directory.Exists(ReplayRoot))
            {
                Directory.Delete(ReplayRoot, recursive: true);
            }
        }
    }

    private sealed class FixedAuthenticator(
        BrokerAuthenticatedClient? client,
        BrokerError? error) : IBrokerConnectionAuthenticator
    {
        internal BrokerAuthenticatedClient? Client { get; } = client;

        public ValueTask<BrokerAuthenticationResult> AuthenticateAsync(
            Stream stream,
            BrokerClientHello hello,
            BrokerConsentOptions options,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                Client is not null
                    ? BrokerAuthenticationResult.Success(Client)
                    : BrokerAuthenticationResult.Failure(error!));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            new(Task.Delay(delay, cancellationToken));
    }

    private sealed class ExpiringHandshakeClock : IClock
    {
        private int _readCount;

        public DateTimeOffset UtcNow => Interlocked.Increment(ref _readCount) <= 3
            ? Now
            : Now.AddMilliseconds(29_950);

        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            new(Task.Delay(delay, cancellationToken));
    }

    private sealed class LimitedDuplexStream(
        Stream inner,
        int maximumReadBytes,
        Exception? terminalReadFailure) : Stream
    {
        private int _bytesRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int remaining = maximumReadBytes - _bytesRead;
            if (remaining <= 0)
            {
                if (terminalReadFailure is not null)
                {
                    throw terminalReadFailure;
                }

                return 0;
            }

            int read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, remaining)], cancellationToken);
            _bytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    }

    private sealed class FailingWriteStream(Stream inner, int failOnWriteCall) : Stream
    {
        private int _writeCallCount;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _writeCallCount) == failOnWriteCall
                ? ValueTask.FromException(new IOException("Injected response write failure."))
                : inner.WriteAsync(buffer, cancellationToken);
    }

    private sealed class NoneEvidence : IReplaySideEffectEvidenceProvider
    {
        public ReplaySideEffectEvidence Inspect(Guid requestId) => ReplaySideEffectEvidence.None;
    }

    private sealed class RolledBackAddHook : IDirectoryAclEditorTestHook
    {
        public bool FailAddPostValidation => true;

        public bool FailRollbackWrite => false;
    }

    private sealed class RecordTrackingRecoveryTransaction(
        RecoveryRecordTransaction inner) : IFolderLockRecoveryTransaction
    {
        internal Guid RecordId { get; private set; }

        public async ValueTask<Result<Guid>> PrepareAsync(
            FolderLockRequest request,
            SessionIdentity sessionIdentity,
            ValidatedDirectory directory,
            RecoveryAclEvidence evidence,
            CancellationToken cancellationToken)
        {
            Result<Guid> result = await inner.PrepareAsync(
                request,
                sessionIdentity,
                directory,
                evidence,
                cancellationToken);
            if (result.IsSuccess)
            {
                RecordId = result.Value;
            }

            return result;
        }

        public ValueTask<Result> MarkAppliedAsync(
            Guid recoveryRecordId,
            RecoveryAclEvidence evidence,
            CancellationToken cancellationToken) =>
            inner.MarkAppliedAsync(recoveryRecordId, evidence, cancellationToken);

        public ValueTask<Result> MarkCleanupPendingAsync(
            Guid recoveryRecordId,
            CancellationToken cancellationToken) =>
            inner.MarkCleanupPendingAsync(recoveryRecordId, cancellationToken);

        public ValueTask<Result> MarkCleanupFailedAsync(
            Guid recoveryRecordId,
            Error error,
            CancellationToken cancellationToken) =>
            inner.MarkCleanupFailedAsync(recoveryRecordId, error, cancellationToken);

        public ValueTask<Result> DeleteAsync(
            Guid recoveryRecordId,
            CancellationToken cancellationToken) =>
            inner.DeleteAsync(recoveryRecordId, cancellationToken);
    }

    private sealed class DeleteFailureRecoveryStorePlatform : IRecoveryStoreFilePlatform
    {
        private readonly WindowsRecoveryStoreFilePlatform _inner = new();

        internal bool DeleteFailureEnabled { get; set; } = true;

        public Result<SafeFileHandle> OpenDirectory(string path) => _inner.OpenDirectory(path);

        public Result<SafeFileHandle> CreateTemporary(
            SafeFileHandle directoryHandle,
            string leafName) => _inner.CreateTemporary(directoryHandle, leafName);

        public Result<SafeFileHandle> OpenExisting(
            SafeFileHandle directoryHandle,
            string leafName) => _inner.OpenExisting(directoryHandle, leafName);

        public Result<RecoveryRecordFileIdentity> GetIdentity(SafeFileHandle handle) =>
            _inner.GetIdentity(handle);

        public Result<NativeMethods.FileAttributeTagInfo> GetAttributes(SafeFileHandle handle) =>
            _inner.GetAttributes(handle);

        public Result<string> GetFinalPath(SafeFileHandle handle) => _inner.GetFinalPath(handle);

        public Result WriteAll(SafeFileHandle handle, ReadOnlyMemory<byte> bytes) =>
            _inner.WriteAll(handle, bytes);

        public Result Flush(SafeFileHandle handle) => _inner.Flush(handle);

        public Result<byte[]> ReadAll(SafeFileHandle handle, int maximumLength) =>
            _inner.ReadAll(handle, maximumLength);

        public Result Rename(
            SafeFileHandle fileHandle,
            SafeFileHandle directoryHandle,
            string targetLeafName,
            bool replaceExisting) => _inner.Rename(
                fileHandle,
                directoryHandle,
                targetLeafName,
                replaceExisting);

        public Result Delete(SafeFileHandle fileHandle) => DeleteFailureEnabled
            ? Result.Failure(new Error(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED,
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED,
                ErrorCategory.UnrecoverableError))
            : _inner.Delete(fileHandle);

        public Result CloseAfterDisposition(SafeFileHandle fileHandle) =>
            _inner.CloseAfterDisposition(fileHandle);

        public Result<RecoveryRecordFileIdentity?> GetLeafIdentity(
            SafeFileHandle directoryHandle,
            string leafName) => _inner.GetLeafIdentity(directoryHandle, leafName);
    }

    private static WindowsFolderPathValidator CreatePathValidator(string testRoot) => new(
        new FolderPathSafetyPolicy(
            Path.Combine(testRoot, "repository"),
            Path.Combine(testRoot, "installation"),
            []));
}
