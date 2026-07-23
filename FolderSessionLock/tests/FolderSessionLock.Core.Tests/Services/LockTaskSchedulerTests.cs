using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using FolderSessionLock.Core.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSessionLock.Core.Tests.Services;

public sealed class LockTaskSchedulerTests
{
    private static readonly DateTimeOffset StartUtc =
        new(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromSeconds(1), TimeSpan.FromDays(30)).Value;

    [Fact]
    public async Task ProcessDueTasksAsync_DifferentExpiries_ProcessesInExpiryOrder()
    {
        SchedulerSystem system = CreateSystem();
        FolderLockTask later = await AddAndActivate(system, @"C:\Tasks\Later", TimeSpan.FromMinutes(20));
        FolderLockTask earlier = await AddAndActivate(system, @"C:\Tasks\Earlier", TimeSpan.FromMinutes(10));
        system.Clock.Advance(TimeSpan.FromMinutes(20));

        Result<int> result = await system.Scheduler.ProcessDueTasksAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.Equal(
            new[] { earlier.Id.Value, later.Id.Value },
            system.Service.RemoveRequests.Select(request => request.TaskId));
    }

    [Fact]
    public async Task ProcessDueTasksAsync_SimultaneousExpiries_ProcessesEveryTask()
    {
        SchedulerSystem system = CreateSystem();
        FolderLockTask first = await AddAndActivate(system, @"C:\Tasks\First", TimeSpan.FromMinutes(10));
        FolderLockTask second = await AddAndActivate(system, @"C:\Tasks\Second", TimeSpan.FromMinutes(10));
        system.Clock.Advance(TimeSpan.FromMinutes(10));

        Result<int> result = await system.Scheduler.ProcessDueTasksAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(first.Id).Value.Status);
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(second.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessDueTasksAsync_OneFailure_DoesNotStopOtherDueTasks()
    {
        Guid failedTaskId = Guid.Empty;
        var service = new RecordingFolderLockService
        {
            RemoveHandler = (taskId, _) => taskId == failedTaskId
                ? Result.Failure(new Error(
                    "test.remove.failed",
                    "Removal failed.",
                    ErrorCategory.RecoverableError))
                : Result.Success(),
        };
        SchedulerSystem system = CreateSystem(service);
        FolderLockTask failed = await AddAndActivate(system, @"C:\Tasks\Failed", TimeSpan.FromMinutes(5));
        failedTaskId = failed.Id.Value;
        FolderLockTask successful = await AddAndActivate(system, @"C:\Tasks\Successful", TimeSpan.FromMinutes(5));
        system.Clock.Advance(TimeSpan.FromMinutes(5));

        Result<int> result = await system.Scheduler.ProcessDueTasksAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(2, service.RemoveCallCount);
        Assert.Equal(LockTaskStatus.UnlockFailed, system.Manager.GetById(failed.Id).Value.Status);
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(successful.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessDueTasksAsync_OneAdvanceAcrossManyExpiries_ProcessesAllDueTasks()
    {
        SchedulerSystem system = CreateSystem();
        FolderLockTask[] tasks = await Task.WhenAll(
            AddAndActivate(system, @"C:\Tasks\One", TimeSpan.FromMinutes(1)),
            AddAndActivate(system, @"C:\Tasks\Two", TimeSpan.FromHours(1)),
            AddAndActivate(system, @"C:\Tasks\Three", TimeSpan.FromDays(7)));
        system.Clock.Advance(TimeSpan.FromDays(14));

        await system.Scheduler.ProcessDueTasksAsync();

        Assert.Equal(3, system.Service.RemoveCallCount);
        Assert.All(tasks, task =>
            Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(task.Id).Value.Status));
    }

    [Fact]
    public async Task RunAsync_CancellationStopsLoopWithoutCompletingActiveTask()
    {
        var logger = new RecordingLogger<LockTaskScheduler>();
        SchedulerSystem system = CreateSystem(logger: logger);
        FolderLockTask active = await AddAndActivate(
            system,
            @"C:\Tasks\Active",
            TimeSpan.FromHours(1));
        using var cancellation = new CancellationTokenSource();

        Task<Result> runTask = system.Scheduler.RunAsync(cancellation.Token).AsTask();
        await system.Clock.DelayScheduled;
        cancellation.Cancel();
        Result result = await runTask;

        Assert.True(result.IsSuccess);
        Assert.Equal(0, system.Service.RemoveCallCount);
        Assert.Equal(LockTaskStatus.Active, system.Manager.GetById(active.Id).Value.Status);
        Assert.Empty(logger.Messages);
        Assert.Empty(logger.Exceptions);
    }

    [Fact]
    public async Task RunAsync_PreCanceledToken_DoesNotChangeTaskState()
    {
        SchedulerSystem system = CreateSystem();
        FolderLockTask created = CreateTask(@"C:\Tasks\Created", TimeSpan.FromMinutes(1));
        system.Manager.Add(created);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Result result = await system.Scheduler.RunAsync(cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(LockTaskStatus.Created, system.Manager.GetById(created.Id).Value.Status);
    }

    [Fact]
    public async Task RunAsync_UsesThirtySecondMonotonicSegmentsAndIgnoresUtcJump()
    {
        SchedulerSystem system = CreateSystem();
        FolderLockTask active = await AddAndActivate(
            system,
            @"C:\Tasks\Segmented",
            TimeSpan.FromMinutes(2));
        using var cancellation = new CancellationTokenSource();
        Task<Result> run = system.Scheduler.RunAsync(cancellation.Token).AsTask();
        await system.Clock.DelayScheduled;

        Assert.Equal(LockTaskScheduler.MaximumDelaySegment, Assert.Single(system.Clock.ScheduledDelays));
        system.Clock.AdvanceWallClock(TimeSpan.FromDays(7));
        system.Clock.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => system.Clock.ScheduledDelays.Count == 2);

        Assert.Equal(
            [LockTaskScheduler.MaximumDelaySegment, LockTaskScheduler.MaximumDelaySegment],
            system.Clock.ScheduledDelays);
        Assert.Equal(LockTaskStatus.Active, system.Manager.GetById(active.Id).Value.Status);
        cancellation.Cancel();
        Assert.True((await run).IsSuccess);
    }

    [Fact]
    public async Task RunAsync_UnexpectedException_LogsOnlySanitizedDiagnostics()
    {
        const string sensitiveMessage =
            @"Sensitive path C:\Users\Person. SID S-1-5-21-123. SDDL D:(D;;DC;;;S-1-5-21-123). token secret-token.\n   at Sensitive.Namespace.Component.Run()";
        var clock = new ThrowingTimestampClock(sensitiveMessage);
        var manager = new LockTaskManager(new ExactFolderPathRelationService());
        var coordinator = new LockTaskCoordinator(
            manager,
            new RecordingFolderLockService(),
            clock,
            NullLogger<LockTaskCoordinator>.Instance);
        var logger = new RecordingLogger<LockTaskScheduler>();
        var scheduler = new LockTaskScheduler(coordinator, clock, logger);

        Result result = await scheduler.RunAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("lock_task.scheduler.loop.exception", result.Error!.Code);
        Assert.Equal(
            "The lock task scheduler loop terminated unexpectedly.",
            result.Error.Message);
        Assert.All(logger.Exceptions, Assert.Null);
        string log = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("lock_task.scheduler.loop.exception", log, StringComparison.Ordinal);
        Assert.Contains(
            "The lock task scheduler loop terminated unexpectedly.",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMessage, log, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), log, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Person", log, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21-123", log, StringComparison.Ordinal);
        Assert.DoesNotContain("D:(D;;DC;;;S-1-5-21-123)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive.Namespace.Component.Run", log, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionDurationPolicy_UsesExactInclusiveBounds()
    {
        LockDurationPolicy policy = LockDurationPolicy.CreateProduction();

        Assert.Equal(TimeSpan.FromMilliseconds(60_000), policy.Minimum);
        Assert.Equal(TimeSpan.FromMilliseconds(86_400_000), policy.Maximum);
        Assert.True(LockDuration.Create(TimeSpan.FromMilliseconds(60_000), policy).IsSuccess);
        Assert.True(LockDuration.Create(TimeSpan.FromMilliseconds(59_999), policy).IsFailure);
        Assert.True(LockDuration.Create(TimeSpan.FromMilliseconds(86_400_000), policy).IsSuccess);
        Assert.True(LockDuration.Create(TimeSpan.FromMilliseconds(86_400_001), policy).IsFailure);
    }

    private static SchedulerSystem CreateSystem(
        RecordingFolderLockService? service = null,
        ILogger<LockTaskScheduler>? logger = null)
    {
        var clock = new ControllableClock(StartUtc);
        var manager = new LockTaskManager(new ExactFolderPathRelationService());
        var lockService = service ?? new RecordingFolderLockService();
        var coordinator = new LockTaskCoordinator(
            manager,
            lockService,
            clock,
            NullLogger<LockTaskCoordinator>.Instance);
        var scheduler = new LockTaskScheduler(
            coordinator,
            clock,
            logger ?? NullLogger<LockTaskScheduler>.Instance);
        return new SchedulerSystem(manager, coordinator, scheduler, lockService, clock);
    }

    private static async Task<FolderLockTask> AddAndActivate(
        SchedulerSystem system,
        string path,
        TimeSpan duration)
    {
        FolderLockTask task = CreateTask(path, duration);
        Assert.True(system.Manager.Add(task).IsSuccess);
        Result<FolderLockTask> activated = await system.Coordinator.ActivateAsync(task.Id);
        Assert.True(activated.IsSuccess);
        return activated.Value;
    }

    private static FolderLockTask CreateTask(string path, TimeSpan duration) =>
        FolderLockTask.Create(
            FolderLockTaskId.New(),
            FolderPath.Create(path).Value,
            LockDuration.Create(duration, DurationPolicy).Value,
            StartUtc).Value;

    private sealed record SchedulerSystem(
        LockTaskManager Manager,
        LockTaskCoordinator Coordinator,
        LockTaskScheduler Scheduler,
        RecordingFolderLockService Service,
        ControllableClock Clock);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ThrowingTimestampClock(string sensitiveMessage) : IClock
    {
        public DateTimeOffset UtcNow => StartUtc;

        public long GetTimestamp() => throw new InvalidOperationException(sensitiveMessage);

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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
