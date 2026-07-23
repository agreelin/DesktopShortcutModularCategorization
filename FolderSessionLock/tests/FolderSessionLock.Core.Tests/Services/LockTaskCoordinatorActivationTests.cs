using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using FolderSessionLock.Core.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSessionLock.Core.Tests.Services;

public sealed class LockTaskCoordinatorActivationTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromMinutes(1), TimeSpan.FromHours(8)).Value;

    [Fact]
    public async Task ActivateAsync_Success_TransitionsToActiveOnce()
    {
        var service = new RecordingFolderLockService();
        (_, LockTaskCoordinator coordinator, FolderLockTask task) = CreateSystem(service);

        Result<FolderLockTask> result = await coordinator.ActivateAsync(task.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(LockTaskStatus.Active, result.Value.Status);
        Assert.Equal(1, service.CreateCallCount);
        Assert.Equal(task.Id.Value, service.CreateRequests.Single().TaskId);
        Assert.Null(result.Value.Error);
    }

    [Fact]
    public async Task ActivateAsync_PlatformFailure_PreservesErrorAndTransitionsToActivationFailed()
    {
        var platformError = new Error(
            "test.activation.failed",
            "Activation failed.",
            ErrorCategory.RecoverableError);
        var service = new RecordingFolderLockService
        {
            CreateHandler = _ => Result<Guid>.Failure(platformError),
        };
        (LockTaskManager manager, LockTaskCoordinator coordinator, FolderLockTask task) =
            CreateSystem(service);

        Result<FolderLockTask> result = await coordinator.ActivateAsync(task.Id);
        FolderLockTask stored = manager.GetById(task.Id).Value;

        Assert.True(result.IsFailure);
        Assert.Same(platformError, result.Error);
        Assert.Equal(LockTaskStatus.ActivationFailed, stored.Status);
        Assert.Same(platformError, stored.Error!.Detail);
    }

    [Fact]
    public async Task ActivateAsync_UnrecoverablePlatformFailure_TransitionsToRecoveryRequired()
    {
        var platformError = new Error(
            "test.activation.unrecoverable",
            "Activation state is unknown.",
            ErrorCategory.UnrecoverableError);
        var service = new RecordingFolderLockService
        {
            CreateHandler = _ => Result<Guid>.Failure(platformError),
        };
        (LockTaskManager manager, LockTaskCoordinator coordinator, FolderLockTask task) =
            CreateSystem(service);

        Result<FolderLockTask> result = await coordinator.ActivateAsync(task.Id);

        Assert.True(result.IsFailure);
        Assert.Same(platformError, result.Error);
        Assert.Equal(LockTaskStatus.RecoveryRequired, manager.GetById(task.Id).Value.Status);
    }

    [Fact]
    public async Task ActivateAsync_DifferentReturnedId_TransitionsToRecoveryRequired()
    {
        var service = new RecordingFolderLockService
        {
            CreateHandler = _ => Result<Guid>.Success(Guid.NewGuid()),
        };
        (LockTaskManager manager, LockTaskCoordinator coordinator, FolderLockTask task) =
            CreateSystem(service);

        Result<FolderLockTask> result = await coordinator.ActivateAsync(task.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("lock_task.activation.task_id_mismatch", result.Error!.Code);
        Assert.Equal(LockTaskStatus.RecoveryRequired, manager.GetById(task.Id).Value.Status);
    }

    [Fact]
    public async Task ActivateAsync_RepeatedCall_DoesNotApplyAgain()
    {
        var service = new RecordingFolderLockService();
        (_, LockTaskCoordinator coordinator, FolderLockTask task) = CreateSystem(service);
        await coordinator.ActivateAsync(task.Id);

        Result<FolderLockTask> second = await coordinator.ActivateAsync(task.Id);

        Assert.True(second.IsFailure);
        Assert.Equal(1, service.CreateCallCount);
    }

    [Fact]
    public async Task ActivateAsync_ConcurrentCalls_ApplyExactlyOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingFolderLockService
        {
            AsyncCreateHandler = async request =>
            {
                entered.SetResult();
                await release.Task;
                return Result<Guid>.Success(request.TaskId);
            },
        };
        (_, LockTaskCoordinator coordinator, FolderLockTask task) = CreateSystem(service);

        Task<Result<FolderLockTask>> first = coordinator.ActivateAsync(task.Id).AsTask();
        await entered.Task;
        Task<Result<FolderLockTask>> second = coordinator.ActivateAsync(task.Id).AsTask();
        release.SetResult();
        Result<FolderLockTask>[] results = await Task.WhenAll(first, second);

        Assert.Single(results.Where(result => result.IsSuccess));
        Assert.Single(results.Where(result => result.IsFailure));
        Assert.Equal(1, service.CreateCallCount);
    }

    [Fact]
    public async Task ActivateAsync_ThrownException_RecordsRecoveryRequired()
    {
        var service = new RecordingFolderLockService
        {
            AsyncCreateHandler = _ => throw new InvalidOperationException("Test platform exception."),
        };
        (LockTaskManager manager, LockTaskCoordinator coordinator, FolderLockTask task) =
            CreateSystem(service);

        Result<FolderLockTask> result = await coordinator.ActivateAsync(task.Id);
        FolderLockTask stored = manager.GetById(task.Id).Value;

        Assert.True(result.IsFailure);
        Assert.Equal("lock_task.activation.exception", result.Error!.Code);
        Assert.Equal(LockTaskStatus.RecoveryRequired, stored.Status);
        Assert.Equal(result.Error.Code, stored.Error!.Detail.Code);
    }

    [Fact]
    public async Task ActivateAsync_UnrepresentableExpectedExpiry_RecordsRecoveryRequiredWithoutThrowing()
    {
        LockDurationPolicy largePolicy = LockDurationPolicy.Create(
            TimeSpan.FromTicks(1),
            TimeSpan.MaxValue).Value;
        LockDuration largeDuration = LockDuration.Create(TimeSpan.MaxValue, largePolicy).Value;
        var service = new RecordingFolderLockService();
        var manager = new LockTaskManager(new ExactFolderPathRelationService());
        FolderLockTask task = FolderLockTask.Create(
            FolderLockTaskId.New(),
            FolderPath.Create(@"C:\Tasks\LargeDuration").Value,
            largeDuration,
            CreatedAtUtc).Value;
        manager.Add(task);
        var clock = new FixedClock(CreatedAtUtc, 500);
        var coordinator = new LockTaskCoordinator(
            manager,
            service,
            clock,
            NullLogger<LockTaskCoordinator>.Instance);
        var scheduler = new LockTaskScheduler(
            coordinator,
            clock,
            NullLogger<LockTaskScheduler>.Instance);

        Result<FolderLockTask> result = await coordinator.ActivateAsync(task.Id);
        Result<int> scanResult = await scheduler.ProcessDueTasksAsync();
        FolderLockTask stored = manager.GetById(task.Id).Value;

        Assert.True(result.IsFailure);
        Assert.Equal("lock_task.expiry.out_of_range", result.Error!.Code);
        Assert.Equal(1, service.CreateCallCount);
        Assert.Equal(LockTaskStatus.RecoveryRequired, stored.Status);
        Assert.Equal(result.Error.Code, stored.Error!.Detail.Code);
        Assert.True(scanResult.IsSuccess);
        Assert.Equal(0, scanResult.Value);
        Assert.Equal(0, service.RemoveCallCount);
    }

    private static (LockTaskManager, LockTaskCoordinator, FolderLockTask) CreateSystem(
        RecordingFolderLockService service)
    {
        var manager = new LockTaskManager(new ExactFolderPathRelationService());
        FolderLockTask task = FolderLockTask.Create(
            FolderLockTaskId.New(),
            FolderPath.Create(@"C:\Tasks\Coordinator").Value,
            LockDuration.Create(TimeSpan.FromMinutes(30), DurationPolicy).Value,
            CreatedAtUtc).Value;
        manager.Add(task);
        var coordinator = new LockTaskCoordinator(
            manager,
            service,
            new FixedClock(CreatedAtUtc, 500),
            NullLogger<LockTaskCoordinator>.Instance);
        return (manager, coordinator, task);
    }
}
