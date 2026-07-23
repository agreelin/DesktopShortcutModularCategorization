using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using FolderSessionLock.Core.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSessionLock.Core.Tests.Services;

public sealed class LockTaskCoordinatorExpirationTests
{
    private static readonly DateTimeOffset StartUtc =
        new(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromSeconds(1), TimeSpan.FromDays(30)).Value;

    [Fact]
    public async Task ProcessDueTasksAsync_BeforeExpiry_RemainsActive()
    {
        TestSystem system = await CreateActiveSystem(TimeSpan.FromMinutes(10));
        system.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));

        Result<int> result = await system.Coordinator.ProcessDueTasksAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value);
        Assert.Equal(0, system.Service.RemoveCallCount);
        Assert.Equal(LockTaskStatus.Active, system.Manager.GetById(system.Task.Id).Value.Status);
        Assert.Equal(TimeSpan.FromTicks(1), system.Manager.GetById(system.Task.Id).Value.GetRemainingTime(system.Clock));
    }

    [Fact]
    public async Task ProcessDueTasksAsync_AtExactExpiry_RemovesWithExpirationAndCompletes()
    {
        TestSystem system = await CreateActiveSystem(TimeSpan.FromMinutes(10));
        system.Clock.Advance(TimeSpan.FromMinutes(10));

        Result<int> result = await system.Coordinator.ProcessDueTasksAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Equal(1, system.Service.RemoveCallCount);
        Assert.Equal((system.Task.Id.Value, LockRemovalIntent.Expiration), system.Service.RemoveRequests.Single());
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(system.Task.Id).Value.Status);
        Assert.Equal(TimeSpan.Zero, system.Manager.GetById(system.Task.Id).Value.GetRemainingTime(system.Clock));
    }

    [Fact]
    public async Task ProcessDueTasksAsync_CrossesExpiryAndRepeatedScans_RemoveOnce()
    {
        TestSystem system = await CreateActiveSystem(TimeSpan.FromMinutes(10));
        system.Clock.Advance(TimeSpan.FromDays(14));

        await system.Coordinator.ProcessDueTasksAsync();
        await system.Coordinator.ProcessDueTasksAsync();
        await system.Coordinator.ProcessDueTasksAsync();

        Assert.Equal(1, system.Service.RemoveCallCount);
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(system.Task.Id).Value.Status);
        Assert.Equal(TimeSpan.Zero, system.Manager.GetById(system.Task.Id).Value.GetRemainingTime(system.Clock));
    }

    [Fact]
    public async Task ProcessDueTasksAsync_ConcurrentScans_RemoveOnce()
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
        TestSystem system = await CreateActiveSystem(TimeSpan.FromMinutes(1), service);
        system.Clock.Advance(TimeSpan.FromMinutes(1));

        Task<Result<int>> first = system.Coordinator.ProcessDueTasksAsync().AsTask();
        await removeEntered.Task;
        Task<Result<int>> second = system.Coordinator.ProcessDueTasksAsync().AsTask();
        releaseRemove.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, service.RemoveCallCount);
        Assert.Equal(LockTaskStatus.Completed, system.Manager.GetById(system.Task.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessDueTasksAsync_RemoveFailure_RecordsUnlockFailed()
    {
        var removeError = new Error(
            "test.remove.failed",
            "Removal failed.",
            ErrorCategory.RecoverableError);
        var service = new RecordingFolderLockService
        {
            RemoveHandler = (_, _) => Result.Failure(removeError),
        };
        TestSystem system = await CreateActiveSystem(TimeSpan.FromMinutes(1), service);
        system.Clock.Advance(TimeSpan.FromMinutes(1));

        Result<int> result = await system.Coordinator.ProcessDueTasksAsync();
        FolderLockTask stored = system.Manager.GetById(system.Task.Id).Value;

        Assert.True(result.IsFailure);
        Assert.Same(removeError, result.Error);
        Assert.Equal(LockTaskStatus.UnlockFailed, stored.Status);
        Assert.Same(removeError, stored.Error!.Detail);
        Assert.NotEqual(LockTaskStatus.Completed, stored.Status);
    }

    [Fact]
    public async Task ProcessDueTasksAsync_UnrecoverableRemoveFailure_RecordsRecoveryRequired()
    {
        var removeError = new Error(
            "test.remove.unrecoverable",
            "Removal state is unknown.",
            ErrorCategory.UnrecoverableError);
        var service = new RecordingFolderLockService
        {
            RemoveHandler = (_, _) => Result.Failure(removeError),
        };
        TestSystem system = await CreateActiveSystem(TimeSpan.FromMinutes(1), service);
        system.Clock.Advance(TimeSpan.FromMinutes(1));

        Result<int> result = await system.Coordinator.ProcessDueTasksAsync();

        Assert.True(result.IsFailure);
        Assert.Same(removeError, result.Error);
        Assert.Equal(
            LockTaskStatus.RecoveryRequired,
            system.Manager.GetById(system.Task.Id).Value.Status);
    }

    [Fact]
    public async Task ProcessDueTasksAsync_RemoveException_RecordsRecoveryRequired()
    {
        var service = new RecordingFolderLockService
        {
            AsyncRemoveHandler = (_, _) => throw new InvalidOperationException("Test remove exception."),
        };
        TestSystem system = await CreateActiveSystem(TimeSpan.FromMinutes(1), service);
        system.Clock.Advance(TimeSpan.FromMinutes(1));

        Result<int> result = await system.Coordinator.ProcessDueTasksAsync();
        FolderLockTask stored = system.Manager.GetById(system.Task.Id).Value;

        Assert.True(result.IsFailure);
        Assert.Equal("lock_task.expiration.exception", result.Error!.Code);
        Assert.Equal(LockTaskStatus.RecoveryRequired, stored.Status);
    }

    [Fact]
    public async Task WallClockChanges_DoNotChangeElapsedDurationOrCauseRemoval()
    {
        TestSystem system = await CreateActiveSystem(TimeSpan.FromHours(1));

        system.Clock.AdvanceWallClock(TimeSpan.FromDays(30));
        await system.Coordinator.ProcessDueTasksAsync();
        system.Clock.AdvanceWallClock(TimeSpan.FromDays(-60));
        await system.Coordinator.ProcessDueTasksAsync();

        Assert.Equal(0, system.Service.RemoveCallCount);
        Assert.Equal(TimeSpan.FromHours(1), system.Manager.GetById(system.Task.Id).Value.GetRemainingTime(system.Clock));
    }

    [Fact]
    public async Task OffsetAndDaylightSavingRepresentations_DoNotChangeUtcOrElapsedDuration()
    {
        TestSystem system = await CreateActiveSystem(TimeSpan.FromHours(1));
        DateTimeOffset sameInstantWithOffset = StartUtc.ToOffset(TimeSpan.FromHours(8));

        system.Clock.SetWallClock(sameInstantWithOffset);
        await system.Coordinator.ProcessDueTasksAsync();
        system.Clock.SetWallClock(sameInstantWithOffset.ToOffset(TimeSpan.FromHours(9)));
        await system.Coordinator.ProcessDueTasksAsync();

        Assert.Equal(StartUtc, system.Clock.UtcNow);
        Assert.Equal(0, system.Service.RemoveCallCount);
        Assert.Equal(TimeSpan.FromHours(1), system.Manager.GetById(system.Task.Id).Value.GetRemainingTime(system.Clock));
    }

    [Fact]
    public async Task ConcurrentActivationAndDueScan_DoesNotRemoveActivatingTask()
    {
        var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingFolderLockService
        {
            AsyncCreateHandler = async request =>
            {
                createEntered.SetResult();
                await releaseCreate.Task;
                return Result<Guid>.Success(request.TaskId);
            },
        };
        TestSystem system = CreateSystem(TimeSpan.FromMinutes(1), service);

        Task<Result<FolderLockTask>> activation = system.Coordinator.ActivateAsync(system.Task.Id).AsTask();
        await createEntered.Task;
        system.Clock.Advance(TimeSpan.FromDays(1));
        await system.Coordinator.ProcessDueTasksAsync();
        releaseCreate.SetResult();
        await activation;

        Assert.Equal(0, service.RemoveCallCount);
        Assert.Equal(LockTaskStatus.Active, system.Manager.GetById(system.Task.Id).Value.Status);
        Assert.Equal(TimeSpan.FromMinutes(1), system.Manager.GetById(system.Task.Id).Value.GetRemainingTime(system.Clock));
    }

    private static async Task<TestSystem> CreateActiveSystem(
        TimeSpan duration,
        RecordingFolderLockService? service = null)
    {
        TestSystem system = CreateSystem(duration, service ?? new RecordingFolderLockService());
        Result<FolderLockTask> result = await system.Coordinator.ActivateAsync(system.Task.Id);
        Assert.True(result.IsSuccess);
        Assert.Equal(StartUtc + duration, result.Value.ExpectedExpiryUtc);
        return system;
    }

    private static TestSystem CreateSystem(TimeSpan duration, RecordingFolderLockService service)
    {
        var clock = new ControllableClock(StartUtc);
        var manager = new LockTaskManager(new ExactFolderPathRelationService());
        FolderLockTask task = FolderLockTask.Create(
            FolderLockTaskId.New(),
            FolderPath.Create(@"C:\Tasks\Expiration").Value,
            LockDuration.Create(duration, DurationPolicy).Value,
            StartUtc).Value;
        manager.Add(task);
        var coordinator = new LockTaskCoordinator(
            manager,
            service,
            clock,
            NullLogger<LockTaskCoordinator>.Instance);
        return new TestSystem(manager, coordinator, service, clock, task);
    }

    private sealed record TestSystem(
        LockTaskManager Manager,
        LockTaskCoordinator Coordinator,
        RecordingFolderLockService Service,
        ControllableClock Clock,
        FolderLockTask Task);
}
