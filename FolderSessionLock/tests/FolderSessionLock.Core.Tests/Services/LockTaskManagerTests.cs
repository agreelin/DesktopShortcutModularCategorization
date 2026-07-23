using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;

namespace FolderSessionLock.Core.Tests.Services;

public sealed class LockTaskManagerTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromMinutes(1), TimeSpan.FromHours(8)).Value;

    [Fact]
    public void Add_StoresTaskAndReturnsReadOnlySnapshots()
    {
        var manager = new LockTaskManager(new ExactRelationService());
        FolderLockTask task = CreateTask(@"C:\Tasks\One");

        Result addResult = manager.Add(task);

        Assert.True(addResult.IsSuccess);
        Assert.Same(task, manager.GetById(task.Id).Value);
        IReadOnlyList<FolderLockTask> all = manager.GetAll();
        Assert.Single(all);
        Assert.False(all is List<FolderLockTask>);
    }

    [Fact]
    public void Add_RejectsDuplicateIdWithoutReplacingTask()
    {
        var manager = new LockTaskManager(new ExactRelationService());
        FolderLockTask original = CreateTask(@"C:\Tasks\One");
        FolderLockTask duplicate = CreateTask(@"C:\Tasks\Two", original.Id);
        manager.Add(original);

        Result result = manager.Add(duplicate);

        Assert.True(result.IsFailure);
        Assert.Equal("lock_task.id.conflict", result.Error!.Code);
        Assert.Same(original, manager.GetById(original.Id).Value);
    }

    [Theory]
    [InlineData(FolderPathRelation.Same)]
    [InlineData(FolderPathRelation.Ancestor)]
    [InlineData(FolderPathRelation.Descendant)]
    public void Add_RejectsEveryOverlappingRelation(FolderPathRelation relation)
    {
        var manager = new LockTaskManager(new FixedRelationService(relation));
        manager.Add(CreateTask(@"C:\Tasks\Existing"));

        Result result = manager.Add(CreateTask(@"C:\Tasks\Requested"));

        Assert.True(result.IsFailure);
        Assert.Equal("lock_task.path.conflict", result.Error!.Code);
        Assert.Single(manager.GetAll());
    }

    [Fact]
    public void CompletedAndActivationFailedTasks_DoNotOccupyPath()
    {
        var manager = new LockTaskManager(new ExactRelationService());
        FolderLockTask completed = CreateTask(@"C:\Tasks\Completed");
        FolderLockTask activationFailed = CreateTask(@"C:\Tasks\Failed");
        manager.Add(completed);
        manager.Add(activationFailed);
        TransitionToCompleted(manager, completed.Id);
        manager.TryTransition(activationFailed.Id, LockTaskStatus.Activating, CreatedAtUtc.AddSeconds(1));
        manager.TryTransition(
            activationFailed.Id,
            LockTaskStatus.ActivationFailed,
            CreatedAtUtc.AddSeconds(2),
            CreateTaskError("activation.failed"));

        Assert.True(manager.Add(CreateTask(@"C:\Tasks\Completed")).IsSuccess);
        Assert.True(manager.Add(CreateTask(@"C:\Tasks\Failed")).IsSuccess);
    }

    [Fact]
    public async Task Add_ConcurrentSamePath_AllowsExactlyOneTask()
    {
        var manager = new LockTaskManager(new ExactRelationService());
        using var start = new ManualResetEventSlim(false);
        Task<Result>[] additions = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return manager.Add(CreateTask(@"C:\Tasks\Concurrent"));
            }))
            .ToArray();

        start.Set();
        Result[] results = await Task.WhenAll(additions);

        Assert.Single(results.Where(result => result.IsSuccess));
        Assert.Single(manager.GetAll());
    }

    [Fact]
    public void TryTransition_NoChange_PreservesSnapshotTimestampAndError()
    {
        var manager = new LockTaskManager(new ExactRelationService());
        FolderLockTask task = CreateTask(@"C:\Tasks\NoChange");
        manager.Add(task);

        Result<LockTaskTransition> result = manager.TryTransition(
            task.Id,
            LockTaskStatus.Created,
            CreatedAtUtc.AddHours(1),
            CreateTaskError("ignored.error"));

        Assert.True(result.IsSuccess);
        Assert.Equal(LockTaskTransitionOutcome.NoChange, result.Value.Outcome);
        Assert.Same(task, result.Value.Task);
        Assert.Equal(CreatedAtUtc, result.Value.Task.StatusChangedAtUtc);
        Assert.Null(result.Value.Task.Error);
    }

    [Fact]
    public async Task ConcurrentReads_ObserveOnlyCompleteImmutableSnapshots()
    {
        var manager = new LockTaskManager(new ExactRelationService());
        FolderLockTask task = CreateTask(@"C:\Tasks\Reads");
        manager.Add(task);
        using var start = new ManualResetEventSlim(false);
        Task[] readers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                for (int index = 0; index < 500; index++)
                {
                    FolderLockTask snapshot = Assert.Single(manager.GetAll());
                    Assert.Equal(task.Id, snapshot.Id);
                    Assert.True(Enum.IsDefined(snapshot.Status));
                    Assert.True(snapshot.StatusChangedAtUtc >= snapshot.CreatedAtUtc);
                }
            }))
            .ToArray();

        start.Set();
        manager.TryTransition(task.Id, LockTaskStatus.Activating, CreatedAtUtc.AddSeconds(1));
        manager.TryTransition(
            task.Id,
            LockTaskStatus.Active,
            CreatedAtUtc.AddSeconds(2),
            startedTimestamp: 100);
        await Task.WhenAll(readers);

        Assert.Equal(LockTaskStatus.Active, manager.GetById(task.Id).Value.Status);
    }

    private static FolderLockTask CreateTask(string path, FolderLockTaskId? id = null) =>
        FolderLockTask.Create(
            id ?? FolderLockTaskId.New(),
            FolderPath.Create(path).Value,
            LockDuration.Create(TimeSpan.FromMinutes(30), DurationPolicy).Value,
            CreatedAtUtc).Value;

    private static LockTaskError CreateTaskError(string code) => new(
        new Error(code, "Test task error.", ErrorCategory.RecoverableError),
        CreatedAtUtc);

    private static void TransitionToCompleted(LockTaskManager manager, FolderLockTaskId id)
    {
        manager.TryTransition(id, LockTaskStatus.Activating, CreatedAtUtc.AddSeconds(1));
        manager.TryTransition(id, LockTaskStatus.Active, CreatedAtUtc.AddSeconds(2), startedTimestamp: 100);
        manager.TryTransition(
            id,
            LockTaskStatus.Unlocking,
            CreatedAtUtc.AddSeconds(3),
            removalIntent: LockRemovalIntent.Expiration);
        manager.TryTransition(id, LockTaskStatus.Completed, CreatedAtUtc.AddSeconds(4));
    }

    private sealed class ExactRelationService : IFolderPathRelationService
    {
        public FolderPathRelation GetRelation(FolderPath existingPath, FolderPath requestedPath) =>
            existingPath == requestedPath ? FolderPathRelation.Same : FolderPathRelation.Unrelated;
    }

    private sealed class FixedRelationService(FolderPathRelation relation) : IFolderPathRelationService
    {
        public FolderPathRelation GetRelation(FolderPath existingPath, FolderPath requestedPath) => relation;
    }
}
