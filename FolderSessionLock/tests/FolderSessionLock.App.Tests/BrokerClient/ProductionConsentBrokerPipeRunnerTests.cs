using System.Security.Principal;
using FolderSessionLock.Broker;
using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Broker.Transport;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.App.Tests.BrokerClient;

public sealed class ProductionConsentBrokerPipeRunnerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 1, 2, 3, TimeSpan.Zero);
    private static readonly Guid RequestId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid TaskId =
        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    [Fact]
    public async Task RunAsync_SuccessfulCreateLockStartsOneSchedulerAndWaitsForCleanup()
    {
        BrokerRequestEnvelope request = CreateLockRequest();
        var runtime = new Runtime(CreateLockSucceeded(request));
        var pipe = new PipeServer(request, new BrokerPipeConnectionResult(true, null));
        var runner = CreateRunner(runtime, pipe);
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.ResponseWritten);
        Assert.Equal(1, pipe.RunCount);
        Assert.Equal(1, runtime.ProcessCount);
        Assert.Equal(BrokerExecutionContext.OrdinaryUi, runtime.ExecutionContext);
        Assert.Equal(1, runtime.SchedulerCount);
        Assert.Equal(1, runtime.StopCount);
    }

    [Fact]
    public async Task RunAsync_GetStatusDoesNotStartScheduler()
    {
        BrokerRequestEnvelope request = GetStatusRequest();
        var runtime = new Runtime(GetStatusSucceeded(request));
        var pipe = new PipeServer(request, new BrokerPipeConnectionResult(true, null));
        var runner = CreateRunner(runtime, pipe);
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.ResponseWritten);
        Assert.Equal(0, runtime.SchedulerCount);
        Assert.Equal(1, runtime.StopCount);
    }

    [Fact]
    public async Task RunAsync_ApplicationFailureResponseRemainsAHandledProtocolResult()
    {
        BrokerRequestEnvelope request = CreateLockRequest();
        var runtime = new Runtime(BrokerExecutionOutcome.FailedWithoutSideEffects(
            BrokerResponseEnvelope.Failed(
                request.RequestId,
                request.Command,
                Now,
                BrokerError.Internal())));
        var pipe = new PipeServer(request, new BrokerPipeConnectionResult(
            true,
            runtime.Outcome.Response.Error));
        var runner = CreateRunner(runtime, pipe);
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.ResponseWritten);
        Assert.Equal(
            ConsentBrokerExitCode.ProtocolHandledOrLifecycleCompleted,
            ConsentBrokerExitPolicy.Map(result));
        Assert.Same(runtime.Outcome, pipe.ProcessedOutcome);
        Assert.Equal(0, runtime.SchedulerCount);
    }

    [Fact]
    public async Task RunAsync_ResponseWriteFailureAfterCreateLockWaitsForLifecycleThenReturnsResponseFailure()
    {
        BrokerRequestEnvelope request = CreateLockRequest();
        var runtime = new Runtime(CreateLockSucceeded(request));
        var pipe = new PipeServer(
            request,
            new BrokerPipeConnectionResult(false, BrokerError.Internal()));
        var runner = CreateRunner(runtime, pipe);
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.ResponseWriteFailed);
        Assert.Equal(1, runtime.SchedulerCount);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(
            ConsentBrokerExitCode.ResponseWriteFailed,
            ConsentBrokerExitPolicy.Map(result));
    }

    [Fact]
    public async Task RunAsync_CleanupFailureOverridesResponseWriteFailure()
    {
        BrokerRequestEnvelope request = CreateLockRequest();
        var runtime = new Runtime(
            CreateLockSucceeded(request),
            cleanup: Result<int>.Failure(new Error(
                BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
                BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
                ErrorCategory.UnrecoverableError)));
        var pipe = new PipeServer(
            request,
            new BrokerPipeConnectionResult(false, BrokerError.Internal()));
        var runner = CreateRunner(runtime, pipe);
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.LifecycleCleanupFailed);
        Assert.False(result.ResponseWriteFailed);
        Assert.Equal(
            ConsentBrokerExitCode.LifecycleCleanupFailed,
            ConsentBrokerExitPolicy.Map(result));
    }

    [Fact]
    public async Task RunAsync_RecoveryRequiredOverridesAValidResponseWithoutChangingIt()
    {
        BrokerRequestEnvelope request = CreateLockRequest();
        BrokerExecutionOutcome outcome = BrokerExecutionOutcome.RecoveryRequired(
            BrokerResponseEnvelope.Failed(
                request.RequestId,
                request.Command,
                Now,
                new BrokerError(
                    BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
                    BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
                    false,
                    null)));
        var runtime = new Runtime(outcome, hasRecoveryRequired: true);
        var pipe = new PipeServer(request, new BrokerPipeConnectionResult(
            true,
            outcome.Response.Error));
        var runner = CreateRunner(runtime, pipe);
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.LifecycleCleanupFailed);
        Assert.Same(outcome, pipe.ProcessedOutcome);
        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED, outcome.Response.Error!.Code);
    }

    [Fact]
    public async Task RunAsync_FailureBeforeApplicationRequestReturnsProtocolExitAndStillStopsLifecycle()
    {
        var runtime = new Runtime(CreateLockSucceeded(CreateLockRequest()));
        var pipe = new PipeServer(
            request: null,
            new BrokerPipeConnectionResult(false, BrokerError.Internal()));
        var runner = CreateRunner(runtime, pipe);
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.ProtocolFailedBeforeResponse);
        Assert.Equal(0, runtime.ProcessCount);
        Assert.Equal(0, runtime.SchedulerCount);
        Assert.Equal(1, runtime.StopCount);
    }

    [Fact]
    public async Task RunAsync_CreatesPipeBeforeProductionSessionAndRunsOnlyOneListener()
    {
        var calls = new List<string>();
        BrokerRequestEnvelope request = GetStatusRequest();
        var runtime = new Runtime(GetStatusSucceeded(request));
        var clock = new FixedClock();
        var factory = new SessionFactory(runtime, clock, calls);
        var pipe = new PipeServer(
            request,
            new BrokerPipeConnectionResult(true, null),
            calls);
        var runner = new ProductionConsentBrokerPipeRunner(factory, pipe, clock);
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.ResponseWritten);
        Assert.Equal(["pipe-create", "session-create", "pipe-run", "pipe-dispose"], calls);
        Assert.Equal(1, pipe.CreateCount);
        Assert.Equal(1, pipe.RunCount);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task RunAsync_PipeCreationFailureDoesNotConstructProductionSession()
    {
        var runtime = new Runtime(GetStatusSucceeded(GetStatusRequest()));
        var clock = new FixedClock();
        var factory = new SessionFactory(runtime, clock);
        var runner = new ProductionConsentBrokerPipeRunner(
            factory,
            new FailingPipeServer(),
            clock);
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.PipeInitializationFailed);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, runtime.StopCount);
    }

    [Fact]
    public async Task RunAsync_SchedulerErrorRemainsInternalWhenCleanupSucceeds()
    {
        BrokerRequestEnvelope request = CreateLockRequest();
        var runtime = new Runtime(
            CreateLockSucceeded(request),
            scheduler: Result.Failure(new Error(
                "lock_task.scheduler.loop.exception",
                "The lock task scheduler loop terminated unexpectedly.",
                ErrorCategory.PlatformError)));
        var runner = CreateRunner(
            runtime,
            new PipeServer(request, new BrokerPipeConnectionResult(true, null)));
        using ConsentBrokerBootstrapIdentity identity = Identity();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.True(result.ResponseWritten);
        Assert.Equal(1, runtime.SchedulerCount);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(
            ConsentBrokerExitCode.ProtocolHandledOrLifecycleCompleted,
            ConsentBrokerExitPolicy.Map(result));
    }

    [Fact]
    public async Task RunAsync_PermanentLoggerFailureWithoutSideEffectsReturnsExit28()
    {
        var runtime = new Runtime(GetStatusSucceeded(GetStatusRequest()));
        var runner = CreateRunner(
            runtime,
            new PipeServer(null, new BrokerPipeConnectionResult(false, BrokerError.Internal())));
        using ConsentBrokerBootstrapIdentity identity = Identity();
        using var logger = new PermanentlyFailedLoggerFactory();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            logger,
            CancellationToken.None);

        Assert.True(result.ProtectedLoggerFailed);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(
            ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure,
            ConsentBrokerExitPolicy.Map(result));
    }

    [Fact]
    public async Task RunAsync_PermanentLoggerFailureAfterSideEffectsCleansUpThenReturnsExit28()
    {
        BrokerRequestEnvelope request = CreateLockRequest();
        var runtime = new Runtime(CreateLockSucceeded(request));
        var runner = CreateRunner(
            runtime,
            new PipeServer(request, new BrokerPipeConnectionResult(false, BrokerError.Internal())));
        using ConsentBrokerBootstrapIdentity identity = Identity();
        using var logger = new PermanentlyFailedLoggerFactory();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            logger,
            CancellationToken.None);

        Assert.True(result.ProtectedLoggerFailed);
        Assert.Equal(1, runtime.SchedulerCount);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(
            ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure,
            ConsentBrokerExitPolicy.Map(result));
    }

    [Fact]
    public async Task RunAsync_CleanupFailureOverridesPermanentLoggerFailure()
    {
        BrokerRequestEnvelope request = CreateLockRequest();
        var runtime = new Runtime(
            CreateLockSucceeded(request),
            cleanup: Result<int>.Failure(new Error(
                BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
                BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
                ErrorCategory.UnrecoverableError)));
        var runner = CreateRunner(
            runtime,
            new PipeServer(request, new BrokerPipeConnectionResult(false, BrokerError.Internal())));
        using ConsentBrokerBootstrapIdentity identity = Identity();
        using var logger = new PermanentlyFailedLoggerFactory();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            logger,
            CancellationToken.None);

        Assert.True(result.LifecycleCleanupFailed);
        Assert.Equal(
            ConsentBrokerExitCode.LifecycleCleanupFailed,
            ConsentBrokerExitPolicy.Map(result));
    }

    [Fact]
    public async Task RunAsync_ValidResponseOverridesLaterPermanentLoggerFailure()
    {
        BrokerRequestEnvelope request = GetStatusRequest();
        var runtime = new Runtime(GetStatusSucceeded(request));
        var runner = CreateRunner(
            runtime,
            new PipeServer(request, new BrokerPipeConnectionResult(true, null)));
        using ConsentBrokerBootstrapIdentity identity = Identity();
        using var logger = new PermanentlyFailedLoggerFactory();

        ConsentBrokerPipeRunResult result = await runner.RunAsync(
            Options(),
            identity,
            logger,
            CancellationToken.None);

        Assert.True(result.ResponseWritten);
        Assert.True(result.ProtectedLoggerFailed);
        Assert.Equal(
            ConsentBrokerExitCode.ProtocolHandledOrLifecycleCompleted,
            ConsentBrokerExitPolicy.Map(result));
    }

    private static ProductionConsentBrokerPipeRunner CreateRunner(
        Runtime runtime,
        PipeServer pipe)
    {
        var clock = new FixedClock();
        return new ProductionConsentBrokerPipeRunner(
            new SessionFactory(runtime, clock),
            pipe,
            clock);
    }

    private static BrokerConsentOptions Options() => new(
        BrokerProtocolConstants.PipeName,
        7,
        RequestId,
        1234,
        123456789);

    private static ConsentBrokerBootstrapIdentity Identity()
    {
        var client = new SessionIdentity("S-1-5-21-1", "S-1-5-5-1-2", 7);
        var broker = new SessionIdentity("S-1-5-21-1", "S-1-5-5-3-4", 7);
        return new ConsentBrokerBootstrapIdentity(
            client,
            broker,
            new SecurityIdentifier(client.LogonSid),
            new SecurityIdentifier(broker.AccountSid),
            new SafeAccessTokenHandle(new nint(1)));
    }

    private static BrokerRequestEnvelope CreateLockRequest() => new(
        BrokerProtocolConstants.ProtocolVersion,
        RequestId,
        BrokerCommand.CreateLock,
        7,
        Now,
        new CreateLockRequest(TaskId, @"C:\FolderSessionLock.Tests\Target", 60_000));

    private static BrokerRequestEnvelope GetStatusRequest() => new(
        BrokerProtocolConstants.ProtocolVersion,
        RequestId,
        BrokerCommand.GetStatus,
        7,
        Now,
        new GetStatusRequest(GetStatusQueryType.CurrentSession, null));

    private static BrokerExecutionOutcome CreateLockSucceeded(BrokerRequestEnvelope request) =>
        BrokerExecutionOutcome.Succeeded(BrokerResponseEnvelope.Succeeded(
            request.RequestId,
            request.Command,
            Now,
            new CreateLockResult(
                TaskId,
                @"C:\FolderSessionLock.Tests\Target",
                LockTaskStatus.Active,
                Now,
                Now.AddMinutes(1),
                60_000,
                60_000,
                Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"),
                false)));

    private static BrokerExecutionOutcome GetStatusSucceeded(BrokerRequestEnvelope request) =>
        BrokerExecutionOutcome.Succeeded(BrokerResponseEnvelope.Succeeded(
            request.RequestId,
            request.Command,
            Now,
            new GetStatusResult(GetStatusQueryType.CurrentSession, [])));

    private sealed class SessionFactory(
        Runtime runtime,
        IClock clock,
        List<string>? calls = null)
        : IConsentBrokerProductionSessionFactory
    {
        internal int CreateCount { get; private set; }

        public ConsentBrokerProductionSession Create(
            ConsentBrokerBootstrapIdentity identity,
            ILoggerFactory loggerFactory,
            IClock suppliedClock)
        {
            CreateCount++;
            calls?.Add("session-create");
            Assert.Same(clock, suppliedClock);
            return new ConsentBrokerProductionSession(
                LockDurationPolicy.CreateProduction(),
                suppliedClock,
                null!,
                null!,
                runtime);
        }
    }

    private sealed class PipeServer(
        BrokerRequestEnvelope? request,
        BrokerPipeConnectionResult result,
        List<string>? calls = null)
        : IConsentBrokerPipeServer,
          IConsentBrokerPipeListener
    {
        internal int CreateCount { get; private set; }

        internal int RunCount { get; private set; }

        internal BrokerExecutionOutcome? ProcessedOutcome { get; private set; }

        public IConsentBrokerPipeListener Create(ConsentBrokerBootstrapIdentity identity)
        {
            CreateCount++;
            calls?.Add("pipe-create");
            return this;
        }

        public async ValueTask<BrokerPipeConnectionResult> RunOnceAsync(
            BrokerConsentOptions options,
            ConsentBrokerProductionSession session,
            Func<BrokerRequestEnvelope, CancellationToken, ValueTask<BrokerExecutionOutcome>> processRequest,
            CancellationToken cancellationToken)
        {
            RunCount++;
            calls?.Add("pipe-run");
            if (request is not null)
            {
                ProcessedOutcome = await processRequest(request, cancellationToken);
            }

            return result;
        }

        public ValueTask DisposeAsync()
        {
            calls?.Add("pipe-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingPipeServer : IConsentBrokerPipeServer
    {
        public IConsentBrokerPipeListener? Create(ConsentBrokerBootstrapIdentity identity) => null;
    }

    private sealed class Runtime(
        BrokerExecutionOutcome outcome,
        Result<int>? cleanup = null,
        Result? scheduler = null,
        bool hasRecoveryRequired = false)
        : IConsentBrokerSessionRuntime
    {
        internal BrokerExecutionOutcome Outcome { get; } = outcome;

        internal int ProcessCount { get; private set; }

        internal int SchedulerCount { get; private set; }

        internal int StopCount { get; private set; }

        internal BrokerExecutionContext? ExecutionContext { get; private set; }

        public bool HasRecoveryRequired { get; } = hasRecoveryRequired;

        public ValueTask<BrokerExecutionOutcome> ProcessAsync(
            BrokerRequestEnvelope request,
            BrokerExecutionContext executionContext,
            CancellationToken cancellationToken)
        {
            ProcessCount++;
            ExecutionContext = executionContext;
            return ValueTask.FromResult(Outcome);
        }

        public Task<Result> RunSchedulerAsync()
        {
            SchedulerCount++;
            return Task.FromResult(scheduler ?? Result.Success());
        }

        public ValueTask<Result<int>> StopAsync()
        {
            StopCount++;
            return ValueTask.FromResult(cleanup ?? Result<int>.Success(0));
        }
    }

    private sealed class PermanentlyFailedLoggerFactory
        : ILoggerFactory,
          IProtectedLoggerHealth
    {
        public bool IsPermanentlyFailed => true;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
