using FolderSessionLock.Broker.Lifecycle;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.Broker.Lifecycle.Tests;

public sealed class BrokerLifecycleControllerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromSeconds(1), TimeSpan.FromDays(30)).Value;

    public static TheoryData<bool, bool> SchedulerAndCleanupResults => new()
    {
        { false, false },
        { false, true },
        { true, false },
        { true, true },
    };

    [Theory]
    [MemberData(nameof(SchedulerAndCleanupResults))]
    public async Task StopAsync_ReturnsCleanupResultForAllSchedulerCleanupCombinations(
        bool schedulerFails,
        bool cleanupFails)
    {
        var schedulerError = new Error(
            "test.scheduler.failed",
            "Sensitive scheduler failure.",
            ErrorCategory.PlatformError);
        var cleanupError = new Error(
            "test.cleanup.failed",
            "Sensitive cleanup failure.",
            ErrorCategory.RecoverableError);
        var scheduler = new ControlledScheduler(schedulerFails
            ? Result.Failure(schedulerError)
            : Result.Success());
        var folderLockService = new RecordingFolderLockService(cleanupFails ? cleanupError : null);
        TestSystem system = await CreateSystem(scheduler, folderLockService);
        Task<Result> schedulerTask = system.Controller.RunSchedulerAsync();

        Result<int> result = await system.Controller.StopAsync();
        await schedulerTask;

        if (cleanupFails)
        {
            Assert.True(result.IsFailure);
            Assert.Same(cleanupError, result.Error);
        }
        else
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value);
        }

        Assert.Equal(1, scheduler.RunCallCount);
        Assert.Equal(1, folderLockService.RemoveCallCount);
    }

    [Fact]
    public async Task StopAsync_SchedulerThrowStillCleansWithoutUsingProductionLoopErrorContract()
    {
        var scheduler = new ControlledScheduler(new IOException("Sensitive scheduler exception."));
        var folderLockService = new RecordingFolderLockService();
        var logger = new RecordingLogger<BrokerLifecycleController>();
        TestSystem system = await CreateSystem(scheduler, folderLockService, logger);
        Task<Result> schedulerTask = system.Controller.RunSchedulerAsync();

        Result<int> result = await system.Controller.StopAsync();
        await Assert.ThrowsAsync<IOException>(() => schedulerTask);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Equal(1, folderLockService.RemoveCallCount);
        string log = string.Join(Environment.NewLine, logger.Messages);
        Assert.DoesNotContain("lock_task.scheduler.loop.exception", log, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "The lock task scheduler loop terminated unexpectedly.",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IOException", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive scheduler exception.", log, StringComparison.Ordinal);
        Assert.All(logger.Exceptions, Assert.Null);
    }

    [Fact]
    public async Task StopAsync_WithoutScheduler_Cleans()
    {
        var folderLockService = new RecordingFolderLockService();
        TestSystem system = await CreateSystem(new ControlledScheduler(Result.Success()), folderLockService);

        Result<int> result = await system.Controller.StopAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Equal(1, folderLockService.RemoveCallCount);
    }

    [Fact]
    public async Task StopAsync_RepeatedAndConcurrentCallsShareResultAndCleanupOnce()
    {
        var folderLockService = new RecordingFolderLockService(blockRemoval: true);
        TestSystem system = await CreateSystem(new ControlledScheduler(Result.Success()), folderLockService);

        Task<Result<int>> first = system.Controller.StopAsync().AsTask();
        await folderLockService.RemoveEntered;
        Task<Result<int>> second = system.Controller.StopAsync().AsTask();
        Task<Result<int>> third = system.Controller.StopAsync().AsTask();
        folderLockService.ReleaseRemoval();
        Result<int>[] results = await Task.WhenAll(first, second, third);

        Assert.Equal(1, folderLockService.RemoveCallCount);
        Assert.Same(results[0], results[1]);
        Assert.Same(results[0], results[2]);
    }

    [Fact]
    public async Task WaitForSessionEndingAndStopAsync_RacesWithStopAndCleansOnce()
    {
        var folderLockService = new RecordingFolderLockService();
        TestSystem system = await CreateSystem(new ControlledScheduler(Result.Success()), folderLockService);
        var firstSource = new ControlledSessionEndingSource();
        var secondSource = new ControlledSessionEndingSource();
        Task<Result<int>> first = system.Controller.WaitForSessionEndingAndStopAsync(firstSource).AsTask();
        Task<Result<int>> second = system.Controller.WaitForSessionEndingAndStopAsync(secondSource).AsTask();

        firstSource.Signal();
        Task<Result<int>> direct = system.Controller.StopAsync().AsTask();
        secondSource.Signal();
        Result<int>[] results = await Task.WhenAll(first, second, direct);

        Assert.Equal(1, folderLockService.RemoveCallCount);
        Assert.Same(results[0], results[1]);
        Assert.Same(results[0], results[2]);
    }

    [Fact]
    public async Task WaitForSessionEndingAndStopAsync_WaitCancellationAfterSignalDoesNotCancelCleanup()
    {
        var folderLockService = new RecordingFolderLockService(blockRemoval: true);
        TestSystem system = await CreateSystem(new ControlledScheduler(Result.Success()), folderLockService);
        var source = new ControlledSessionEndingSource();
        using var cancellation = new CancellationTokenSource();
        Task<Result<int>> stop = system.Controller.WaitForSessionEndingAndStopAsync(
            source,
            cancellation.Token).AsTask();

        source.Signal();
        await folderLockService.RemoveEntered;
        cancellation.Cancel();
        folderLockService.ReleaseRemoval();
        Result<int> result = await stop;

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Equal(1, folderLockService.RemoveCallCount);
    }

    [Fact]
    public async Task RunSchedulerAsync_ConcurrentCallsStartSchedulerOnce()
    {
        var scheduler = new ControlledScheduler(Result.Success());
        TestSystem system = await CreateSystem(scheduler, new RecordingFolderLockService());

        Task<Result>[] tasks = Enumerable.Range(0, 8)
            .Select(_ => system.Controller.RunSchedulerAsync())
            .ToArray();

        Assert.All(tasks, task => Assert.Same(tasks[0], task));
        Assert.Equal(1, scheduler.RunCallCount);
        Result<int> stop = await system.Controller.StopAsync();
        Assert.True(stop.IsSuccess);
    }

    private static async Task<TestSystem> CreateSystem(
        ControlledScheduler scheduler,
        RecordingFolderLockService folderLockService,
        ILogger<BrokerLifecycleController>? lifecycleLogger = null)
    {
        var clock = new FixedClock();
        var manager = new LockTaskManager(new UnrelatedPaths());
        var coordinator = new LockTaskCoordinator(
            manager,
            folderLockService,
            clock,
            new RecordingLogger<LockTaskCoordinator>());
        FolderLockTask task = FolderLockTask.Create(
            FolderLockTaskId.New(),
            FolderPath.Create(@"C:\Tasks\Lifecycle").Value,
            LockDuration.Create(TimeSpan.FromHours(1), DurationPolicy).Value,
            Now).Value;
        Assert.True(manager.Add(task).IsSuccess);
        Assert.True((await coordinator.ActivateAsync(task.Id)).IsSuccess);
        return new TestSystem(
            new BrokerLifecycleController(
                scheduler,
                coordinator,
                lifecycleLogger ?? new RecordingLogger<BrokerLifecycleController>()));
    }

    private sealed record TestSystem(BrokerLifecycleController Controller);

    private sealed class ControlledScheduler : ILockTaskScheduler
    {
        private readonly Result? _result;
        private readonly Exception? _exception;
        private int _runCallCount;

        internal ControlledScheduler(Result result)
        {
            _result = result;
        }

        internal ControlledScheduler(Exception exception)
        {
            _exception = exception;
        }

        internal int RunCallCount => Volatile.Read(ref _runCallCount);

        public ValueTask<Result<int>> ProcessDueTasksAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<int>.Success(0));

        public async ValueTask<Result> RunAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runCallCount);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return _result!;
        }
    }

    private sealed class RecordingFolderLockService : IFolderLockService
    {
        private readonly Error? _removeError;
        private readonly TaskCompletionSource _removeEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRemoval =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockRemoval;
        private int _removeCallCount;

        internal RecordingFolderLockService(Error? removeError = null, bool blockRemoval = false)
        {
            _removeError = removeError;
            _blockRemoval = blockRemoval;
        }

        internal int RemoveCallCount => Volatile.Read(ref _removeCallCount);

        internal Task RemoveEntered => _removeEntered.Task;

        public ValueTask<Result<Guid>> CreateLockAsync(
            FolderLockRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<Guid>.Success(request.TaskId));

        public async ValueTask<Result> RemoveLockAsync(
            Guid taskId,
            LockRemovalIntent intent,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _removeCallCount);
            _removeEntered.TrySetResult();
            if (_blockRemoval)
            {
                await _releaseRemoval.Task;
            }

            return _removeError is null ? Result.Success() : Result.Failure(_removeError);
        }

        internal void ReleaseRemoval() => _releaseRemoval.TrySetResult();
    }

    private sealed class ControlledSessionEndingSource : IBrokerSessionEndingSource
    {
        private readonly TaskCompletionSource _signal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WaitAsync(CancellationToken cancellationToken = default) =>
            new(_signal.Task.WaitAsync(cancellationToken));

        internal void Signal() => _signal.TrySetResult();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class UnrelatedPaths : IFolderPathRelationService
    {
        public FolderPathRelation GetRelation(FolderPath first, FolderPath second) =>
            FolderPathRelation.Unrelated;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<string> Messages { get; } = [];
        internal List<Exception?> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
