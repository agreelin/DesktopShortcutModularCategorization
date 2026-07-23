using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Services;

public sealed class LockTaskManager
{
    private readonly object _gate = new();
    private readonly IFolderPathRelationService _folderPathRelationService;
    private readonly Dictionary<FolderLockTaskId, FolderLockTask> _tasks = [];

    public LockTaskManager(IFolderPathRelationService folderPathRelationService)
    {
        _folderPathRelationService = folderPathRelationService
            ?? throw new ArgumentNullException(nameof(folderPathRelationService));
    }

    public Result Add(FolderLockTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            if (_tasks.ContainsKey(task.Id))
            {
                return Result.Failure(new Error(
                    "lock_task.id.conflict",
                    "A task with the same ID already exists.",
                    ErrorCategory.ValidationFailed));
            }

            foreach (FolderLockTask existingTask in _tasks.Values.Where(IsPathOccupying))
            {
                FolderPathRelation relation = _folderPathRelationService.GetRelation(
                    existingTask.FolderPath,
                    task.FolderPath);
                if (relation != FolderPathRelation.Unrelated)
                {
                    return Result.Failure(new Error(
                        "lock_task.path.conflict",
                        $"The requested folder path conflicts with an existing {relation} task path.",
                        ErrorCategory.ValidationFailed));
                }
            }

            _tasks.Add(task.Id, task);
            return Result.Success();
        }
    }

    public Result<FolderLockTask> GetById(FolderLockTaskId taskId)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(taskId, out FolderLockTask? task)
                ? Result<FolderLockTask>.Success(task)
                : Result<FolderLockTask>.Failure(NotFound(taskId));
        }
    }

    public IReadOnlyList<FolderLockTask> GetOccupyingTasks()
    {
        lock (_gate)
        {
            return _tasks.Values.Where(IsPathOccupying).ToArray();
        }
    }

    public IReadOnlyList<FolderLockTask> GetAll()
    {
        lock (_gate)
        {
            return _tasks.Values.ToArray();
        }
    }

    public Result<LockTaskTransition> TryTransition(
        FolderLockTaskId taskId,
        LockTaskStatus targetStatus,
        DateTimeOffset transitionedAtUtc,
        LockTaskError? error = null,
        long? startedTimestamp = null,
        LockRemovalIntent? removalIntent = null)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out FolderLockTask? task))
            {
                return Result<LockTaskTransition>.Failure(NotFound(taskId));
            }

            Result<LockTaskTransition> transition = task.TryTransition(
                targetStatus,
                transitionedAtUtc,
                error,
                startedTimestamp,
                removalIntent);
            if (transition.IsSuccess && transition.Value.Outcome == LockTaskTransitionOutcome.Applied)
            {
                _tasks[taskId] = transition.Value.Task;
            }

            return transition;
        }
    }

    private static bool IsPathOccupying(FolderLockTask task) => task.Status is
        LockTaskStatus.Created
        or LockTaskStatus.Activating
        or LockTaskStatus.Active
        or LockTaskStatus.Unlocking
        or LockTaskStatus.UnlockFailed
        or LockTaskStatus.RecoveryRequired;

    private static Error NotFound(FolderLockTaskId taskId) => new(
        "lock_task.not_found",
        $"Folder lock task {taskId} was not found.",
        ErrorCategory.ValidationFailed);
}
