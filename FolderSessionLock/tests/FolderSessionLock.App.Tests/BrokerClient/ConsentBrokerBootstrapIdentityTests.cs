using FolderSessionLock.Broker.Security;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.App.Tests.BrokerClient;

public sealed class ConsentBrokerBootstrapIdentityTests
{
    [Fact]
    public async Task VerifyAsync_ReadsBrokerThenBindsClientProcessTokenAndSessions()
    {
        var process = new ClientProcess(
            true,
            Result<ulong>.Success(123456789),
            TokenSnapshot(ClientIdentity()));
        var platform = new IdentityPlatform(
            Result<SessionIdentity>.Success(BrokerIdentity()),
            Result<IInitiatingClientProcess>.Success(process));
        var verifier = new ConsentBrokerBootstrapIdentityVerifier(platform);

        ConsentBrokerBootstrapIdentityResult result = await verifier.VerifyAsync(
            Options(),
            default);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ConsentBrokerExitCode.ProtocolHandledOrLifecycleCompleted,
            result.ExitCode);
        Assert.Equal(ClientIdentity(), result.Identity!.InitiatingClient);
        Assert.Equal(BrokerIdentity(), result.Identity.Broker);
        Assert.Equal("S-1-5-5-1-2", result.Identity.InitiatingLogonSid.Value);
        Assert.Equal("S-1-5-21-1", result.Identity.BrokerAccountSid.Value);
        Assert.Equal(["broker", "open", "alive", "creation", "identity", "dispose"], platform.Calls);
        Assert.Equal(1234u, platform.OpenedProcessId);
        result.Identity.Dispose();
    }

    [Fact]
    public async Task VerifyAsync_MapsUnavailableProcessOrIdentityToExit21()
    {
        Error error = IdentityError();
        var platforms = new IConsentBrokerBootstrapIdentityPlatform[]
        {
            new IdentityPlatform(
                Result<SessionIdentity>.Failure(error),
                Result<IInitiatingClientProcess>.Failure(error)),
            new IdentityPlatform(
                Result<SessionIdentity>.Success(BrokerIdentity()),
                Result<IInitiatingClientProcess>.Failure(error)),
            new IdentityPlatform(
                Result<SessionIdentity>.Success(BrokerIdentity()),
                Result<IInitiatingClientProcess>.Success(new ClientProcess(
                    false,
                    Result<ulong>.Success(123456789),
                    TokenSnapshot(ClientIdentity())))),
            new IdentityPlatform(
                Result<SessionIdentity>.Success(BrokerIdentity()),
                Result<IInitiatingClientProcess>.Success(new ClientProcess(
                    true,
                    Result<ulong>.Failure(error),
                    TokenSnapshot(ClientIdentity())))),
            new IdentityPlatform(
                Result<SessionIdentity>.Success(BrokerIdentity()),
                Result<IInitiatingClientProcess>.Success(new ClientProcess(
                    true,
                    Result<ulong>.Success(123456789),
                    Result<InitiatingClientTokenSnapshot>.Failure(error)))),
        };

        foreach (IConsentBrokerBootstrapIdentityPlatform platform in platforms)
        {
            ConsentBrokerBootstrapIdentityResult result =
                await new ConsentBrokerBootstrapIdentityVerifier(platform)
                    .VerifyAsync(Options(), default);
            AssertFailure(
                result,
                ConsentBrokerExitCode.InitiatingClientIdentityUnavailable);
        }
    }

    [Fact]
    public async Task VerifyAsync_CreationFileTimeMismatchReturnsExit22BeforeTokenRead()
    {
        var process = new ClientProcess(
            true,
            Result<ulong>.Success(123456788),
            TokenSnapshot(ClientIdentity()));
        var verifier = new ConsentBrokerBootstrapIdentityVerifier(new IdentityPlatform(
            Result<SessionIdentity>.Success(BrokerIdentity()),
            Result<IInitiatingClientProcess>.Success(process)));

        ConsentBrokerBootstrapIdentityResult result = await verifier.VerifyAsync(
            Options(),
            default);

        AssertFailure(result, ConsentBrokerExitCode.InitiatingClientProcessMismatch);
        Assert.Equal(0, process.IdentityReads);
    }

    [Fact]
    public async Task VerifyAsync_CrossAccountReturnsExit20BeforePipeIdentityCreation()
    {
        SessionIdentity otherAccount = ClientIdentity() with { AccountSid = "S-1-5-21-2" };
        var verifier = Verifier(otherAccount, BrokerIdentity());

        ConsentBrokerBootstrapIdentityResult result = await verifier.VerifyAsync(
            Options(),
            default);

        AssertFailure(
            result,
            ConsentBrokerExitCode.CrossAccountElevationNotSupported);
    }

    [Theory]
    [InlineData(8, 7)]
    [InlineData(7, 8)]
    [InlineData(-1, 7)]
    public async Task VerifyAsync_SessionMismatchFailsBeforePipeCreation(
        int clientSessionId,
        int brokerSessionId)
    {
        var verifier = Verifier(
            ClientIdentity() with { WindowsSessionId = clientSessionId },
            BrokerIdentity() with { WindowsSessionId = brokerSessionId });

        ConsentBrokerBootstrapIdentityResult result = await verifier.VerifyAsync(
            Options(),
            default);

        AssertFailure(
            result,
            ConsentBrokerExitCode.InitiatingClientIdentityUnavailable);
    }

    private static ConsentBrokerBootstrapIdentityVerifier Verifier(
        SessionIdentity client,
        SessionIdentity broker) => new(new IdentityPlatform(
            Result<SessionIdentity>.Success(broker),
            Result<IInitiatingClientProcess>.Success(new ClientProcess(
                true,
                Result<ulong>.Success(123456789),
                TokenSnapshot(client)))));

    private static BrokerConsentOptions Options() => new(
        BrokerProtocolConstants.PipeName,
        7,
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        1234,
        123456789);

    private static SessionIdentity ClientIdentity() => new(
        "S-1-5-21-1",
        "S-1-5-5-1-2",
        7);

    private static SessionIdentity BrokerIdentity() => new(
        "S-1-5-21-1",
        "S-1-5-5-3-4",
        7);

    private static Result<InitiatingClientTokenSnapshot> TokenSnapshot(
        SessionIdentity identity) =>
        Result<InitiatingClientTokenSnapshot>.Success(new(
            identity,
            new SafeAccessTokenHandle(new nint(1))));

    private static Error IdentityError() => new(
        BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE,
        "The client identity could not be verified.",
        ErrorCategory.UnrecoverableError);

    private static void AssertFailure(
        ConsentBrokerBootstrapIdentityResult result,
        ConsentBrokerExitCode exitCode)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Identity);
        Assert.Equal(exitCode, result.ExitCode);
    }

    private sealed class IdentityPlatform(
        Result<SessionIdentity> broker,
        Result<IInitiatingClientProcess> process)
        : IConsentBrokerBootstrapIdentityPlatform
    {
        internal List<string> Calls { get; } = [];

        internal uint? OpenedProcessId { get; private set; }

        public ValueTask<Result<SessionIdentity>> ReadBrokerIdentityAsync(
            CancellationToken cancellationToken)
        {
            Calls.Add("broker");
            return ValueTask.FromResult(broker);
        }

        public Result<IInitiatingClientProcess> OpenInitiatingClientProcess(uint processId)
        {
            Calls.Add("open");
            OpenedProcessId = processId;
            if (process.IsSuccess && process.Value is ClientProcess client)
            {
                client.Calls = Calls;
            }

            return process;
        }
    }

    private sealed class ClientProcess(
        bool alive,
        Result<ulong> creationTime,
        Result<InitiatingClientTokenSnapshot> identity) : IInitiatingClientProcess
    {
        internal List<string>? Calls { get; set; }

        internal int IdentityReads { get; private set; }

        public bool IsAlive
        {
            get
            {
                Calls?.Add("alive");
                return alive;
            }
        }

        public Result<ulong> ReadCreationFileTime()
        {
            Calls?.Add("creation");
            return creationTime;
        }

        public Result<InitiatingClientTokenSnapshot> ReadIdentityAndRetainToken()
        {
            Calls?.Add("identity");
            IdentityReads++;
            return identity;
        }

        public void Dispose() => Calls?.Add("dispose");
    }
}
