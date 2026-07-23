using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using FolderSessionLock.Core.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSessionLock.Core.Tests.Services;

public sealed class LockTaskCoordinatorLifecycleTests
{
    private static readonly DateTimeOffset StartUtc =
        new(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromSeconds(1), TimeSpan.FromDays(30)).Value;

    [Fact]
    public async Task ProcessAdministrativeCleanupAsync_OrdersByStartedTimestampThenTaskId()
    {
        var service = new RecordingFolderLockService();
        TestSystem system = CreateSystem(service);
        FolderLockTask later = await AddAndActivate(
            system,
            new Guid("00000000-0000-4000-8000-000000000001"),
            @"C:\Tasks\Later");
        Assert.IsType<ControllableClock>(system.Clock).Advance(TimeSpan.FromTicks(1));
        FolderLockTask secondAtSameTimestamp = await AddAndActivate(
            system,
            new Guid("00000000-0000-4000-8000-000000000003"),
            @"C:\Tasks\SecondAtSameTimestamp");
        FolderLockTask firstAtSameTimestamp = await AddAndActivate(
            system,
            new Guid("00000000-0000-4000-8000-000000000002"),
            @"C:\Tasks\FirstAtSameTimestamp");

        Result<int> result = await system.Coordinator.ProcessAdministrativeCleanupAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value);
        Assert.Equal(
            new[] { later.Id.Value, firstAtSameTimestamp.Id.Value, secondAtSameTimestamp.Id.Value },
            service.RemoveRequests.Select(request => request.TaskId));
        Assert.All(service.RemoveRequests, request =>
            Assert.Equal(LockRemovalIntent.AdministrativeCleanup, request.Intent));
    }

    [Fact]
    public async Task ProcessAdministrativeCleanupAsync_ActiveAndUnlockFailed_CompleteAndCountSuccesses()
    {
        TestSystem system = CreateSystem(new RecordingFolderLockService());
        FolderLockTask active = await AddAndActivate(system, Guid.NewGuid(), @"C:\Tasks\Active");
        FolderLockTask retry = await AddAndActivate(system, Guid.NewGuid(), @"C:\Tasks\Retry");
        TransitionToUnlockFailed(system, retry);

        Result<int> result = await system.Coordinator.ProcessAdministrativeCleanupAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(active.Id).Value.Status);
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(retry.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessAdministrativeCleanupAsync_FirstErrorIsStableAndLaterErrorsDoNotStopTraversal()
    {
        var firstError = new Error("test.cleanup.first", "First cleanup failed.", ErrorCategory.RecoverableError);
        var laterError = new Error("test.cleanup.later", "Later cleanup failed.", ErrorCategory.RecoverableError);
        Guid firstId = new("00000000-0000-4000-8000-000000000001");
        Guid laterId = new("00000000-0000-4000-8000-000000000002");
        var service = new RecordingFolderLockService
        {
            RemoveHandler = (taskId, _) => taskId == firstId
                ? Result.Failure(firstError)
                : taskId == laterId
                    ? Result.Failure(laterError)
                    : Result.Success(),
        };
        TestSystem system = CreateSystem(service);
        FolderLockTask successful = await AddAndActivate(
            system,
            new Guid("00000000-0000-4000-8000-000000000003"),
            @"C:\Tasks\Successful");
        FolderLockTask later = await AddAndActivate(system, laterId, @"C:\Tasks\LaterError");
        FolderLockTask first = await AddAndActivate(system, firstId, @"C:\Tasks\FirstError");

        Result<int> result = await system.Coordinator.ProcessAdministrativeCleanupAsync();

        Assert.True(result.IsFailure);
        Assert.Same(firstError, result.Error);
        Assert.Equal(3, service.RemoveCallCount);
        Assert.Equal(new[] { first.Id.Value, later.Id.Value, successful.Id.Value },
            service.RemoveRequests.Select(request => request.TaskId));
        Assert.Equal(LockTaskStatus.UnlockFailed, system.Manager.GetById(first.Id).Value.Status);
        Assert.Equal(LockTaskStatus.UnlockFailed, system.Manager.GetById(later.Id).Value.Status);
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(successful.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessAdministrativeCleanupAsync_UnrecoverableFailure_EntersRecoveryRequired()
    {
        var removeError = new Error(
            "test.cleanup.unrecoverable",
            "Cleanup state is unknown.",
            ErrorCategory.UnrecoverableError);
        var service = new RecordingFolderLockService
        {
            RemoveHandler = (_, _) => Result.Failure(removeError),
        };
        TestSystem system = CreateSystem(service);
        FolderLockTask task = await AddAndActivate(system, Guid.NewGuid(), @"C:\Tasks\RecoveryRequired");

        Result<int> result = await system.Coordinator.ProcessAdministrativeCleanupAsync();

        Assert.True(result.IsFailure);
        Assert.Same(removeError, result.Error);
        Assert.Equal(LockTaskStatus.RecoveryRequired, system.Manager.GetById(task.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessAdministrativeCleanupAsync_RemoveException_UsesExactErrorAndRecoveryRequired()
    {
        var service = new RecordingFolderLockService
        {
            AsyncRemoveHandler = (_, _) => throw new IOException("Sensitive remove exception."),
        };
        TestSystem system = CreateSystem(service);
        FolderLockTask task = await AddAndActivate(system, Guid.NewGuid(), @"C:\Tasks\RemoveException");

        Result<int> result = await system.Coordinator.ProcessAdministrativeCleanupAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("lock_task.administrative_cleanup.exception", result.Error!.Code);
        Assert.Equal(
            "The administrative cleanup ended without a confirmed result.",
            result.Error.Message);
        Assert.Equal(ErrorCategory.UnrecoverableError, result.Error.Category);
        Assert.Equal(LockTaskStatus.RecoveryRequired, system.Manager.GetById(task.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessAdministrativeCleanupAsync_StateUpdateException_UsesExactErrorAndRecoveryRequired()
    {
        var clock = new ThrowingUtcClock(StartUtc);
        TestSystem system = CreateSystem(new RecordingFolderLockService(), clock);
        FolderLockTask task = await AddAndActivate(system, Guid.NewGuid(), @"C:\Tasks\StateUpdateException");
        clock.ThrowOnAccess(2);

        Result<int> result = await system.Coordinator.ProcessAdministrativeCleanupAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("lock_task.administrative_cleanup.state_update_failed", result.Error!.Code);
        Assert.Equal(
            "The lock was removed but its completed state could not be recorded.",
            result.Error.Message);
        Assert.Equal(ErrorCategory.UnrecoverableError, result.Error.Category);
        Assert.Equal(LockTaskStatus.RecoveryRequired, system.Manager.GetById(task.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessAdministrativeCleanupAsync_SkipsIneligibleStates()
    {
        TestSystem system = CreateSystem(new RecordingFolderLockService());
        FolderLockTask completed = await AddAndActivate(system, Guid.NewGuid(), @"C:\Tasks\Completed");
        Assert.True(system.Manager.TryTransition(
            completed.Id,
            LockTaskStatus.Unlocking,
            StartUtc,
            removalIntent: LockRemovalIntent.AdministrativeCleanup).IsSuccess);
        Assert.True(system.Manager.TryTransition(completed.Id, LockTaskStatus.Completed, StartUtc).IsSuccess);
        FolderLockTask created = CreateTask(Guid.NewGuid(), @"C:\Tasks\Created");
        Assert.True(system.Manager.Add(created).IsSuccess);

        Result<int> result = await system.Coordinator.ProcessAdministrativeCleanupAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value);
        Assert.Equal(0, system.Service.RemoveCallCount);
    }

    [Fact]
    public async Task ProcessAdministrativeCleanupAsync_ConcurrentCalls_RemoveEachTaskOnce()
    {
        var removeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingFolderLockService
        {
            AsyncRemoveHandler = async (_, _) =>
            {
                removeEntered.TrySetResult();
                await releaseRemove.Task;
                return Result.Success();
            },
        };
        TestSystem system = CreateSystem(service);
        FolderLockTask task = await AddAndActivate(system, Guid.NewGuid(), @"C:\Tasks\ConcurrentCleanup");

        Task<Result<int>> first = system.Coordinator.ProcessAdministrativeCleanupAsync().AsTask();
        await removeEntered.Task;
        Task<Result<int>> second = system.Coordinator.ProcessAdministrativeCleanupAsync().AsTask();
        releaseRemove.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, service.RemoveCallCount);
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(task.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessAdministrativeCleanupAsync_LogsProtectedSummaryAndSanitizedErrors()
    {
        var firstError = new Error(
            "test.cleanup.first",
            "Sensitive first cleanup message.",
            ErrorCategory.UnrecoverableError);
        var laterError = new Error(
            "test.cleanup.later",
            "Sensitive later cleanup message.",
            ErrorCategory.RecoverableError);
        Guid firstId = new("00000000-0000-4000-8000-000000000001");
        Guid laterId = new("00000000-0000-4000-8000-000000000002");
        var service = new RecordingFolderLockService
        {
            RemoveHandler = (taskId, _) => Result.Failure(taskId == firstId ? firstError : laterError),
        };
        var logger = new RecordingLogger<LockTaskCoordinator>();
        TestSystem system = CreateSystem(service, logger: logger);
        await AddAndActivate(system, laterId, @"C:\Tasks\LoggedLater");
        await AddAndActivate(system, firstId, @"C:\Tasks\LoggedFirst");

        await system.Coordinator.ProcessAdministrativeCleanupAsync();

        string log = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains(firstError.Code, log, StringComparison.Ordinal);
        Assert.Contains(laterError.Code, log, StringComparison.Ordinal);
        Assert.Contains("IsFirstError: True", log, StringComparison.Ordinal);
        Assert.Contains("IsFirstError: False", log, StringComparison.Ordinal);
        Assert.Contains("FullyTraversed: True", log, StringComparison.Ordinal);
        Assert.Contains("RecoveryRequired: True", log, StringComparison.Ordinal);
        Assert.DoesNotContain(firstError.Message, log, StringComparison.Ordinal);
        Assert.DoesNotContain(laterError.Message, log, StringComparison.Ordinal);
        Assert.All(logger.Exceptions, Assert.Null);
    }

    private static TestSystem CreateSystem(
        RecordingFolderLockService service,
        IClock? clock = null,
        ILogger<LockTaskCoordinator>? logger = null)
    {
        IClock testClock = clock ?? new ControllableClock(StartUtc);
        var manager = new LockTaskManager(new ExactFolderPathRelationService());
        var coordinator = new LockTaskCoordinator(
            manager,
            service,
            testClock,
            logger ?? NullLogger<LockTaskCoordinator>.Instance);
        return new TestSystem(manager, coordinator, service, testClock);
    }

    private static async Task<FolderLockTask> AddAndActivate(
        TestSystem system,
        Guid id,
        string path)
    {
        FolderLockTask task = CreateTask(id, path);
        Assert.True(system.Manager.Add(task).IsSuccess);
        Result<FolderLockTask> result = await system.Coordinator.ActivateAsync(task.Id);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static FolderLockTask CreateTask(Guid id, string path) => FolderLockTask.Create(
        FolderLockTaskId.Create(id).Value,
        FolderPath.Create(path).Value,
        LockDuration.Create(TimeSpan.FromHours(1), DurationPolicy).Value,
        StartUtc).Value;

    private static void TransitionToUnlockFailed(TestSystem system, FolderLockTask task)
    {
        Assert.True(system.Manager.TryTransition(
            task.Id,
            LockTaskStatus.Unlocking,
            StartUtc,
            removalIntent: LockRemovalIntent.Expiration).IsSuccess);
        var error = new LockTaskError(
            new Error("test.unlock.failed", "Unlock failed.", ErrorCategory.RecoverableError),
            StartUtc);
        Assert.True(system.Manager.TryTransition(
            task.Id,
            LockTaskStatus.UnlockFailed,
            StartUtc,
            error).IsSuccess);
    }

    private sealed record TestSystem(
        LockTaskManager Manager,
        LockTaskCoordinator Coordinator,
        RecordingFolderLockService Service,
        IClock Clock);

    private sealed class ThrowingUtcClock(DateTimeOffset utcNow) : IClock
    {
        private int _accessCount;
        private int _throwOnAccess = int.MaxValue;

        public DateTimeOffset UtcNow => Interlocked.Increment(ref _accessCount) == _throwOnAccess
            ? throw new InvalidOperationException("Sensitive clock exception.")
            : utcNow;

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        internal void ThrowOnAccess(int access)
        {
            Volatile.Write(ref _accessCount, 0);
            Volatile.Write(ref _throwOnAccess, access);
        }
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
