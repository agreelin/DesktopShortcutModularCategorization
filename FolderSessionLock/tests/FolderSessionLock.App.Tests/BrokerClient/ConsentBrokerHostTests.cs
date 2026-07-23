using System.Security.Principal;
using FolderSessionLock.Broker;
using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.App.Tests.BrokerClient;

public sealed class ConsentBrokerHostTests
{
    private static readonly Guid InstanceId =
        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    [Fact]
    public async Task RunAsync_InitializesProtectedLoggerBeforeIdentityAndPipe()
    {
        var calls = new List<string>();
        var logger = new LoggerFactory(calls);
        ConsentBrokerBootstrapIdentityResult verifiedIdentity = SuccessIdentity();
        var identity = new IdentityVerifier(calls, verifiedIdentity);
        var pipe = new PipeRunner(
            calls,
            new ConsentBrokerPipeRunResult(ResponseWritten: true));
        var host = new ConsentBrokerHost(
            logger,
            identity,
            pipe,
            () => InstanceId);

        ConsentBrokerExitCode exitCode = await host.RunAsync(Options());

        Assert.Equal(ConsentBrokerExitCode.ProtocolHandledOrLifecycleCompleted, exitCode);
        Assert.Equal(["logger", "identity", "pipe", "logger-dispose"], calls);
        Assert.Equal(ProtectedLoggerMode.ConsentBroker, logger.Mode);
        Assert.Equal(InstanceId, logger.InstanceId);
        Assert.Same(verifiedIdentity.Identity, pipe.Identity);
    }

    [Fact]
    public async Task RunAsync_LoggerFailureReturnsExit28BeforeIdentityOrPipe()
    {
        var calls = new List<string>();
        var host = new ConsentBrokerHost(
            new LoggerFactory(calls, fail: true),
            new IdentityVerifier(calls, SuccessIdentity()),
            new PipeRunner(calls, new ConsentBrokerPipeRunResult(ResponseWritten: true)),
            () => InstanceId);

        ConsentBrokerExitCode exitCode = await host.RunAsync(Options());

        Assert.Equal(ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure, exitCode);
        Assert.Equal(["logger"], calls);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    public async Task RunAsync_BootstrapFailureReturnsItsFixedExitWithoutCreatingPipe(
        int expectedExit)
    {
        var calls = new List<string>();
        var host = new ConsentBrokerHost(
            new LoggerFactory(calls),
            new IdentityVerifier(
                calls,
                ConsentBrokerBootstrapIdentityResult.Failure(
                    (ConsentBrokerExitCode)expectedExit)),
            new PipeRunner(calls, new ConsentBrokerPipeRunResult(InternalFailure: true)),
            () => InstanceId);

        ConsentBrokerExitCode exitCode = await host.RunAsync(Options());

        Assert.Equal((ConsentBrokerExitCode)expectedExit, exitCode);
        Assert.Equal(["logger", "identity", "logger-dispose"], calls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 23)]
    [InlineData(2, 24)]
    [InlineData(3, 25)]
    [InlineData(4, 26)]
    [InlineData(5, 27)]
    [InlineData(6, 28)]
    public async Task RunAsync_MapsOnlyTheFixedPipeAndLifecycleExitCodes(
        int outcome,
        int expectedExit)
    {
        var calls = new List<string>();
        var host = new ConsentBrokerHost(
            new LoggerFactory(calls),
            new IdentityVerifier(calls, SuccessIdentity()),
            new PipeRunner(calls, PipeResult(outcome)),
            () => InstanceId);

        ConsentBrokerExitCode exitCode = await host.RunAsync(Options());

        Assert.Equal((ConsentBrokerExitCode)expectedExit, exitCode);
    }

    [Fact]
    public void ExitPolicy_EnforcesCleanupThenResponseThenProtocolPriority()
    {
        Assert.Equal(
            ConsentBrokerExitCode.LifecycleCleanupFailed,
            ConsentBrokerExitPolicy.Map(new(
                ResponseWritten: true,
                ProtocolFailedBeforeResponse: true,
                ResponseWriteFailed: true,
                LifecycleCleanupFailed: true,
                InternalFailure: true)));
        Assert.Equal(
            ConsentBrokerExitCode.ResponseWriteFailed,
            ConsentBrokerExitPolicy.Map(new(
                ProtocolFailedBeforeResponse: true,
                ResponseWriteFailed: true,
                InternalFailure: true)));
        Assert.Equal(
            ConsentBrokerExitCode.ProtocolFailedBeforeResponse,
            ConsentBrokerExitPolicy.Map(new(
                ProtocolFailedBeforeResponse: true,
                InternalFailure: true)));
        Assert.Equal(
            ConsentBrokerExitCode.ProtocolHandledOrLifecycleCompleted,
            ConsentBrokerExitPolicy.Map(new(ResponseWritten: true)));
    }

    [Fact]
    public async Task RunAsync_UnexpectedFailureReturnsExit28AndDisposesLogger()
    {
        var calls = new List<string>();
        var host = new ConsentBrokerHost(
            new LoggerFactory(calls),
            new IdentityVerifier(calls, SuccessIdentity()),
            new PipeRunner(calls, new InvalidOperationException()),
            () => InstanceId);

        ConsentBrokerExitCode exitCode = await host.RunAsync(Options());

        Assert.Equal(ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure, exitCode);
        Assert.Equal(["logger", "identity", "pipe", "logger-dispose"], calls);
    }

    [Fact]
    public async Task RunAsync_EmptyLoggerInstanceIdFailsClosed()
    {
        var calls = new List<string>();
        var host = new ConsentBrokerHost(
            new LoggerFactory(calls),
            new IdentityVerifier(calls, SuccessIdentity()),
            new PipeRunner(calls, new ConsentBrokerPipeRunResult(ResponseWritten: true)),
            () => Guid.Empty);

        ConsentBrokerExitCode exitCode = await host.RunAsync(Options());

        Assert.Equal(ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure, exitCode);
        Assert.Empty(calls);
    }

    private static ConsentBrokerPipeRunResult PipeResult(int value) => value switch
    {
        0 => new(ResponseWritten: true),
        1 => new(PipeInitializationFailed: true),
        2 => new(ClientConnectTimeout: true),
        3 => new(ProtocolFailedBeforeResponse: true),
        4 => new(ResponseWriteFailed: true),
        5 => new(LifecycleCleanupFailed: true),
        6 => new(InternalFailure: true),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static BrokerConsentOptions Options() => new(
        BrokerProtocolConstants.PipeName,
        7,
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        1234,
        123456789);

    private static ConsentBrokerBootstrapIdentityResult SuccessIdentity()
    {
        var client = new SessionIdentity("S-1-5-21-1", "S-1-5-5-1-2", 7);
        var broker = new SessionIdentity("S-1-5-21-1", "S-1-5-5-3-4", 7);
        return ConsentBrokerBootstrapIdentityResult.Success(new(
            client,
            broker,
            new SecurityIdentifier(client.LogonSid),
            new SecurityIdentifier(broker.AccountSid),
            new SafeAccessTokenHandle(new nint(1))));
    }

    private sealed class LoggerFactory(List<string> calls, bool fail = false)
        : IProtectedLoggerFactory
    {
        internal ProtectedLoggerMode? Mode { get; private set; }

        internal Guid? InstanceId { get; private set; }

        public Result<ILoggerFactory> Create(ProtectedLoggerMode mode, Guid instanceId)
        {
            calls.Add("logger");
            Mode = mode;
            InstanceId = instanceId;
            return fail
                ? Result<ILoggerFactory>.Failure(new Error(
                    BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
                    "The protected diagnostic logger could not be initialized.",
                    ErrorCategory.UnrecoverableError))
                : Result<ILoggerFactory>.Success(new DisposableLoggerFactory(calls));
        }
    }

    private sealed class DisposableLoggerFactory(List<string> calls) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new NullLogger();

        public void Dispose() => calls.Add("logger-dispose");
    }

    private sealed class NullLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class IdentityVerifier(
        List<string> calls,
        ConsentBrokerBootstrapIdentityResult result)
        : IConsentBrokerBootstrapIdentityVerifier
    {
        public ValueTask<ConsentBrokerBootstrapIdentityResult> VerifyAsync(
            BrokerConsentOptions options,
            CancellationToken cancellationToken)
        {
            calls.Add("identity");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PipeRunner : IConsentBrokerPipeRunner
    {
        private readonly List<string> _calls;
        private readonly ConsentBrokerPipeRunResult? _result;
        private readonly Exception? _exception;

        internal PipeRunner(List<string> calls, ConsentBrokerPipeRunResult result)
        {
            _calls = calls;
            _result = result;
        }

        internal PipeRunner(List<string> calls, Exception exception)
        {
            _calls = calls;
            _exception = exception;
        }

        internal ConsentBrokerBootstrapIdentity? Identity { get; private set; }

        public ValueTask<ConsentBrokerPipeRunResult> RunAsync(
            BrokerConsentOptions options,
            ConsentBrokerBootstrapIdentity identity,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
        {
            _calls.Add("pipe");
            Identity = identity;
            return _exception is null
                ? ValueTask.FromResult(_result!)
                : ValueTask.FromException<ConsentBrokerPipeRunResult>(_exception);
        }
    }
}
