using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class RecoveryServiceOrchestratorTests
{
    [Fact]
    public async Task StartAsync_ScansOnceReportsRunningReadyAndStopCompletes()
    {
        using ServiceContext context = ServiceContext.Create();

        Task<RecoveryServiceState> first = context.Orchestrator.StartAsync();
        Task<RecoveryServiceState> second = context.Orchestrator.StartAsync();
        RecoveryServiceState state = await first;

        Assert.Same(first, second);
        Assert.Equal(RecoveryServiceState.Ready, state);
        Assert.Equal(1, context.Verifier.CallCount);
        Assert.Equal(
            [RecoveryServiceState.StartPending, RecoveryServiceState.Preflight, RecoveryServiceState.Scanning, RecoveryServiceState.Ready],
            context.Reporter.Snapshots.Select(snapshot => snapshot.State));
        Assert.True(context.Reporter.Snapshots[^1].IsRunning);
        Assert.Equal(RecoveryReadinessState.Ready, context.Publisher.Snapshots[^1].State);

        await context.Orchestrator.StopAsync();

        Assert.Equal(RecoveryServiceState.Stopped, context.Reporter.Snapshots[^1].State);
        Assert.Equal(RecoveryReadinessState.Stopping, context.Publisher.Snapshots[^1].State);
        Assert.True(context.Publisher.Snapshots[^1].RecoveryBlocking);
        Assert.Equal(1, context.Publisher.DeleteCount);
        Assert.Equal(
            Enumerable.Range(1, context.Reporter.Snapshots.Count),
            context.Reporter.Snapshots.Select(snapshot => snapshot.Checkpoint));
    }

    [Fact]
    public async Task RunningService_PublishesTenSecondHeartbeatWithoutRescanning()
    {
        var clock = new HeartbeatClock(
            new DateTimeOffset(2026, 7, 22, 1, 0, 0, TimeSpan.Zero));
        using ServiceContext context = ServiceContext.Create(heartbeatClock: clock);

        Assert.Equal(RecoveryServiceState.Ready, await context.Orchestrator.StartAsync());
        int before = context.Publisher.Snapshots.Count;
        RecoveryReadinessSnapshot initial = context.Publisher.Snapshots[^1];
        await clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => context.Publisher.Snapshots.Count == before + 1);

        RecoveryReadinessSnapshot heartbeat = context.Publisher.Snapshots[^1];
        Assert.Equal(initial.Sequence + 1, heartbeat.Sequence);
        Assert.Equal(initial.State, heartbeat.State);
        Assert.Equal(initial.ScanCompletedUtc, heartbeat.ScanCompletedUtc);
        Assert.Equal(initial.RemainingRecordCount, heartbeat.RemainingRecordCount);
        Assert.Equal(1, context.Verifier.CallCount);
        await context.Orchestrator.StopAsync();
    }

    [Fact]
    public async Task StartAsync_ReportsRunningRecoveryBlockedForArtifacts()
    {
        using ServiceContext context = ServiceContext.Create(addInvalidArtifact: true);

        RecoveryServiceState state = await context.Orchestrator.StartAsync();

        Assert.Equal(RecoveryServiceState.RecoveryBlocked, state);
        Assert.True(context.Reporter.Snapshots[^1].IsRunning);
        Assert.Equal(RecoveryReadinessState.RecoveryBlocked, context.Publisher.Snapshots[^1].State);
        Assert.True(context.Publisher.Snapshots[^1].RecoveryBlocking);
        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_INVALID, context.Publisher.Snapshots[^1].PrimaryErrorCode);
    }

    [Fact]
    public async Task StartAsync_PublisherFailureStopsWithoutReportingRunning()
    {
        using ServiceContext context = ServiceContext.Create(publisherFails: true);

        RecoveryServiceState state = await context.Orchestrator.StartAsync();

        Assert.Equal(RecoveryServiceState.Stopped, state);
        Assert.DoesNotContain(context.Reporter.Snapshots, snapshot => snapshot.IsRunning);
        Assert.Equal(RecoveryServiceState.Stopped, context.Reporter.Snapshots[^1].State);
    }

    [Fact]
    public async Task StartAsync_ProtectedLoggerFailurePublishesRecoveryBlockedAndNeverScans()
    {
        var logger = new FailingLoggerFactory(failOnWrite: 1);
        using ServiceContext context = ServiceContext.Create(loggerFactory: logger);

        RecoveryServiceState state = await context.Orchestrator.StartAsync();

        Assert.Equal(RecoveryServiceState.Stopped, state);
        Assert.Equal(0, context.Verifier.CallCount);
        Assert.Equal(RecoveryReadinessState.RecoveryBlocked, context.Publisher.Snapshots[^1].State);
        Assert.True(context.Publisher.Snapshots[^1].RecoveryBlocking);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            context.Publisher.Snapshots[^1].PrimaryErrorCode);
        Assert.Equal(RecoveryServiceState.Stopped, context.Reporter.Snapshots[^1].State);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            context.Reporter.Snapshots[^1].ErrorCode);
    }

    [Fact]
    public async Task Heartbeat_ProtectedLoggerFailureBlocksReadinessAndRequestsControlledStop()
    {
        var clock = new HeartbeatClock(
            new DateTimeOffset(2026, 7, 22, 1, 0, 0, TimeSpan.Zero));
        var logger = new FailingLoggerFactory(failOnWrite: 3);
        using ServiceContext context = ServiceContext.Create(
            heartbeatClock: clock,
            loggerFactory: logger);
        Assert.Equal(RecoveryServiceState.Ready, await context.Orchestrator.StartAsync());
        await clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromSeconds(10));
        await context.Orchestrator.WaitForStopRequestAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RecoveryReadinessState.RecoveryBlocked, context.Publisher.Snapshots[^1].State);
        Assert.True(context.Publisher.Snapshots[^1].RecoveryBlocking);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            context.Publisher.Snapshots[^1].PrimaryErrorCode);
        await context.Orchestrator.StopAsync();
    }

    [Fact]
    public async Task TwentyFourHourProtectedLogMaintenanceFailureBlocksReadinessAndRequestsStop()
    {
        var clock = new MaintenanceClock(
            new DateTimeOffset(2026, 7, 22, 1, 0, 0, TimeSpan.Zero));
        var logger = new MaintenanceLoggerFactory();
        using ServiceContext context = ServiceContext.Create(
            heartbeatClock: clock,
            loggerFactory: logger);
        Assert.Equal(RecoveryServiceState.Ready, await context.Orchestrator.StartAsync());
        await clock.MaintenanceEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        clock.ReleaseMaintenance.TrySetResult();
        await context.Orchestrator.WaitForStopRequestAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, logger.MaintenanceCount);
        Assert.Equal(RecoveryReadinessState.RecoveryBlocked, context.Publisher.Snapshots[^1].State);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            context.Publisher.Snapshots[^1].PrimaryErrorCode);
        await context.Orchestrator.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WaitsForTheInFlightCriticalSectionAndNeverReturnsToRunning()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        try
        {
            var batch = new BlockingBatchRunner(root);
            var publisher = new RecordingPublisher(null);
            var reporter = new RecordingReporter();
            var orchestrator = new RecoveryServiceOrchestrator(batch, publisher, reporter);
            Task<RecoveryServiceState> start = orchestrator.StartAsync();
            await batch.Started.Task;

            ValueTask stop = orchestrator.StopAsync();
            await Task.Yield();
            Assert.False(stop.IsCompleted);

            batch.Release.TrySetResult();
            await stop;
            Assert.Equal(RecoveryServiceState.Stopping, await start);
            int stoppingIndex = reporter.Snapshots.FindIndex(
                snapshot => snapshot.State == RecoveryServiceState.Stopping);
            Assert.True(stoppingIndex >= 0);
            Assert.DoesNotContain(
                reporter.Snapshots.Skip(stoppingIndex + 1),
                snapshot => snapshot.IsRunning);
            Assert.Equal(RecoveryServiceState.Stopped, reporter.Snapshots[^1].State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task StopAsync_WaitsForTheInFlightCriticalSectionWhenPublicationFailsOrTokenIsCancelled(
        bool publisherFails,
        bool reporterFails,
        bool cancelStopToken)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        try
        {
            var batch = new BlockingBatchRunner(root);
            var publisher = new RecordingPublisher(
                publisherFails ? RecoveryReadinessState.Stopping : null);
            var reporter = new RecordingReporter(
                reporterFails ? RecoveryServiceState.Stopping : null);
            var orchestrator = new RecoveryServiceOrchestrator(batch, publisher, reporter);
            Task<RecoveryServiceState> start = orchestrator.StartAsync();
            await batch.Started.Task;
            using var cancellation = new CancellationTokenSource();
            if (cancelStopToken)
            {
                cancellation.Cancel();
            }

            Task stop = orchestrator.StopAsync(cancellation.Token).AsTask();
            await Task.Yield();
            Assert.False(stop.IsCompleted);

            batch.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<Exception>(() => stop);
            Assert.Equal(RecoveryServiceState.Stopping, await start);
            Assert.DoesNotContain(
                reporter.Snapshots.SkipWhile(snapshot => snapshot.State != RecoveryServiceState.Stopping).Skip(1),
                snapshot => snapshot.IsRunning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ServiceContext : IDisposable
    {
        private ServiceContext(
            string root,
            CountingVerifier verifier,
            RecordingPublisher publisher,
            RecordingReporter reporter,
            RecoveryServiceOrchestrator orchestrator)
        {
            Root = root;
            Verifier = verifier;
            Publisher = publisher;
            Reporter = reporter;
            Orchestrator = orchestrator;
        }

        internal string Root { get; }
        internal CountingVerifier Verifier { get; }
        internal RecordingPublisher Publisher { get; }
        internal RecordingReporter Reporter { get; }
        internal RecoveryServiceOrchestrator Orchestrator { get; }

        internal static ServiceContext Create(
            bool addInvalidArtifact = false,
            bool publisherFails = false,
            IClock? heartbeatClock = null,
            ILoggerFactory? loggerFactory = null)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "FolderSessionLock.Tests",
                Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(root);
            if (addInvalidArtifact)
            {
                File.WriteAllBytes(Path.Combine(root, "invalid"), []);
            }

            var verifier = new CountingVerifier();
            var store = RecoveryTestData.CreateStore(root);
            var cleanup = new RecoveryRecordAclCleanup(
                store,
                new WindowsFolderPathValidator(new FolderPathSafetyPolicy(
                    Path.Combine(root, "repository"),
                    Path.Combine(root, "installation"),
                    [])),
                new FolderSessionLock.Windows.Security.DirectoryAclEditor(),
                new FixedClock());
            var batch = new RecoveryBatchRunner(
                verifier,
                [new(ProtectedPathKind.RecoveryRecordsDirectory, root)],
                RecoveryTestData.CreateEnumerator(root),
                cleanup);
            var publisher = new RecordingPublisher(
                publisherFails ? RecoveryReadinessState.Starting : null);
            var reporter = new RecordingReporter();
            return new ServiceContext(
                root,
                verifier,
                publisher,
                reporter,
                new RecoveryServiceOrchestrator(
                    batch,
                    publisher,
                    reporter,
                    Guid.Parse("11111111-2222-4333-8444-555555555555"),
                    heartbeatClock,
                    loggerFactory));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class CountingVerifier : IProtectedPathSecurityVerifier
    {
        internal int CallCount { get; private set; }

        public ValueTask<ProtectedPathSecurityCheckResult> VerifyAsync(
            ProtectedPathSecurityCheckRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new ProtectedPathSecurityCheckResult(true, null));
        }
    }

    private sealed class RecordingPublisher(RecoveryReadinessState? failingState) : IRecoveryReadinessStore
    {
        internal List<RecoveryReadinessSnapshot> Snapshots { get; } = [];
        internal int DeleteCount { get; private set; }

        public ValueTask PublishAsync(
            RecoveryReadinessSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.State == failingState)
            {
                return ValueTask.FromException(new IOException("publisher unavailable"));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<RecoveryReadinessSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Snapshots.Count == 0
                ? ValueTask.FromException<RecoveryReadinessSnapshot>(new IOException("missing"))
                : ValueTask.FromResult(Snapshots[^1]);

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            DeleteCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingReporter(RecoveryServiceState? failingState = null)
        : IRecoveryServiceStatusReporter
    {
        internal List<RecoveryServiceStatusSnapshot> Snapshots { get; } = [];

        public ValueTask ReportAsync(
            RecoveryServiceStatusSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.State == failingState)
            {
                return ValueTask.FromException(new IOException("status reporter unavailable"));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class BlockingBatchRunner : RecoveryBatchRunner
    {
        internal BlockingBatchRunner(string root)
            : base(
                new CountingVerifier(),
                [new(ProtectedPathKind.RecoveryRecordsDirectory, root)],
                RecoveryTestData.CreateEnumerator(root),
                new RecoveryRecordAclCleanup(
                    RecoveryTestData.CreateStore(root),
                    new WindowsFolderPathValidator(new FolderPathSafetyPolicy(
                        Path.Combine(root, "repository"),
                        Path.Combine(root, "installation"),
                        [])),
                    new FolderSessionLock.Windows.Security.DirectoryAclEditor(),
                    new FixedClock()))
        {
        }

        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal override async ValueTask<RecoveryRunSummary> RunAsync(
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task;
            return new RecoveryRunSummary(
                1, 0, 0, 0, 0, 0, 1, 0, 0, 1, true, BrokerErrorCodes.FSL_E_OPERATION_CANCELLED);
        }
    }

    private sealed class FailingLoggerFactory(int failOnWrite)
        : ILoggerFactory,
          IProtectedLoggerHealth
    {
        private readonly int _failOnWrite = failOnWrite;
        private int _writeCount;

        public bool IsPermanentlyFailed { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(FailingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (Interlocked.Increment(ref owner._writeCount) >= owner._failOnWrite)
                {
                    owner.IsPermanentlyFailed = true;
                }
            }
        }
    }

    private sealed class MaintenanceLoggerFactory
        : ILoggerFactory,
          IProtectedLoggerHealth,
          IProtectedLogMaintenance
    {
        internal int MaintenanceCount { get; private set; }
        public TimeSpan MaintenanceInterval => TimeSpan.FromHours(24);
        public bool IsPermanentlyFailed { get; private set; }

        public Result RunMaintenance()
        {
            MaintenanceCount++;
            IsPermanentlyFailed = true;
            return Result.Failure(new Error(
                BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
                BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
                ErrorCategory.UnrecoverableError));
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class MaintenanceClock(DateTimeOffset utcNow) : IClock
    {
        internal TaskCompletionSource MaintenanceEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseMaintenance { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset UtcNow { get; } = utcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            if (delay == TimeSpan.FromHours(24))
            {
                MaintenanceEntered.TrySetResult();
                await ReleaseMaintenance.Task.WaitAsync(cancellationToken);
                return;
            }

            Assert.Equal(RecoveryReadinessPolicy.HeartbeatInterval, delay);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class HeartbeatClock(DateTimeOffset utcNow) : IClock
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _delayCount;

        internal TaskCompletionSource DelayEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(RecoveryReadinessPolicy.HeartbeatInterval, delay);
            DelayEntered.TrySetResult();
            if (Interlocked.Increment(ref _delayCount) == 1)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            UtcNow += elapsed;
            _release.TrySetResult();
        }
    }
}
