using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.Core.Services;

public sealed class LockTaskCoordinator
{
    private readonly LockTaskManager _taskManager;
    private readonly IFolderLockService _folderLockService;
    private readonly IClock _clock;
    private readonly ILogger<LockTaskCoordinator> _logger;

    public LockTaskCoordinator(
        LockTaskManager taskManager,
        IFolderLockService folderLockService,
        IClock clock,
        ILogger<LockTaskCoordinator> logger)
    {
        _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
        _folderLockService = folderLockService ?? throw new ArgumentNullException(nameof(folderLockService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<Result<FolderLockTask>> ActivateAsync(
        FolderLockTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        Result<LockTaskTransition> activating = _taskManager.TryTransition(
            taskId,
            LockTaskStatus.Activating,
            _clock.UtcNow);
        if (activating.IsFailure)
        {
            return Result<FolderLockTask>.Failure(activating.Error!);
        }

        if (activating.Value.Outcome == LockTaskTransitionOutcome.NoChange)
        {
            return Result<FolderLockTask>.Failure(new Error(
                "lock_task.activation.already_started",
                "The folder lock task activation has already started.",
                ErrorCategory.ValidationFailed));
        }

        FolderLockTask task = activating.Value.Task;
        Result<Guid> createResult;
        try
        {
            createResult = await _folderLockService.CreateLockAsync(
                new FolderLockRequest(task.Id.Value, task.FolderPath.Value, task.Duration.Value),
                cancellationToken);
        }
        catch (Exception exception)
        {
            var error = new Error(
                "lock_task.activation.exception",
                "The platform lock operation ended without a confirmed result.",
                ErrorCategory.UnrecoverableError);
            Result<LockTaskTransition> failureTransition =
                TransitionToFailure(task.Id, LockTaskStatus.RecoveryRequired, error);
            if (failureTransition.IsFailure)
            {
                return Result<FolderLockTask>.Failure(failureTransition.Error!);
            }
            _logger.LogError(
                exception,
                "Task {TaskId} entered {TaskStatus} with error {ErrorCode}.",
                task.Id.Value,
                LockTaskStatus.RecoveryRequired,
                error.Code);
            return Result<FolderLockTask>.Failure(error);
        }

        if (createResult.IsFailure)
        {
            LockTaskStatus failureStatus = createResult.Error!.Category == ErrorCategory.UnrecoverableError
                ? LockTaskStatus.RecoveryRequired
                : LockTaskStatus.ActivationFailed;
            Result<LockTaskTransition> failureTransition =
                TransitionToFailure(task.Id, failureStatus, createResult.Error);
            if (failureTransition.IsFailure)
            {
                return Result<FolderLockTask>.Failure(failureTransition.Error!);
            }
            _logger.LogWarning(
                "Task {TaskId} entered {TaskStatus} with error {ErrorCode}.",
                task.Id.Value,
                failureStatus,
                createResult.Error!.Code);
            return Result<FolderLockTask>.Failure(createResult.Error);
        }

        if (createResult.Value != task.Id.Value)
        {
            var error = new Error(
                "lock_task.activation.task_id_mismatch",
                "The platform lock operation returned a different task ID.",
                ErrorCategory.UnrecoverableError);
            Result<LockTaskTransition> failureTransition =
                TransitionToFailure(task.Id, LockTaskStatus.RecoveryRequired, error);
            if (failureTransition.IsFailure)
            {
                return Result<FolderLockTask>.Failure(failureTransition.Error!);
            }
            _logger.LogError(
                "Task {TaskId} entered {TaskStatus} with error {ErrorCode}.",
                task.Id.Value,
                LockTaskStatus.RecoveryRequired,
                error.Code);
            return Result<FolderLockTask>.Failure(error);
        }

        Result<LockTaskTransition> active;
        DateTimeOffset? stateUpdateUtc = null;
        try
        {
            stateUpdateUtc = _clock.UtcNow;
            active = _taskManager.TryTransition(
                task.Id,
                LockTaskStatus.Active,
                stateUpdateUtc.Value,
                startedTimestamp: _clock.GetTimestamp());
        }
        catch (Exception exception)
        {
            var error = new Error(
                "lock_task.activation.state_update_exception",
                "The lock was created but recording its active state raised an exception.",
                ErrorCategory.UnrecoverableError);
            Result<LockTaskTransition> failureTransition = TransitionToFailure(
                task.Id,
                LockTaskStatus.RecoveryRequired,
                error,
                stateUpdateUtc ?? task.StatusChangedAtUtc);
            _logger.LogError(
                exception,
                "Task {TaskId} entered {TaskStatus} with error {ErrorCode}.",
                task.Id.Value,
                LockTaskStatus.RecoveryRequired,
                error.Code);
            return failureTransition.IsSuccess
                ? Result<FolderLockTask>.Failure(error)
                : Result<FolderLockTask>.Failure(failureTransition.Error!);
        }

        if (active.IsFailure)
        {
            Result<LockTaskTransition> failureTransition =
                TransitionToFailure(
                    task.Id,
                    LockTaskStatus.RecoveryRequired,
                    active.Error!,
                    stateUpdateUtc.Value);
            if (failureTransition.IsFailure)
            {
                return Result<FolderLockTask>.Failure(failureTransition.Error!);
            }
            _logger.LogError(
                "Task {TaskId} entered {TaskStatus} with error {ErrorCode}.",
                task.Id.Value,
                LockTaskStatus.RecoveryRequired,
                active.Error!.Code);
            return Result<FolderLockTask>.Failure(active.Error);
        }

        return Result<FolderLockTask>.Success(active.Value.Task);
    }

    public async ValueTask<Result<int>> ProcessDueTasksAsync(
        CancellationToken cancellationToken = default)
    {
        long nowTimestamp = _clock.GetTimestamp();
        FolderLockTask[] dueTasks = _taskManager.GetAll()
            .Where(task => task.Status == LockTaskStatus.Active && task.StartedTimestamp is not null)
            .Select(task => new
            {
                Task = task,
                Remaining = task.Duration.Value - _clock.GetElapsedTime(
                    task.StartedTimestamp!.Value,
                    nowTimestamp),
            })
            .Where(item => item.Remaining <= TimeSpan.Zero)
            .OrderBy(item => item.Remaining)
            .ThenBy(item => item.Task.StartedTimestamp)
            .ThenBy(item => item.Task.Id.Value)
            .Select(item => item.Task)
            .ToArray();

        int processedCount = 0;
        Error? firstError = null;
        foreach (FolderLockTask task in dueTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result expirationResult = await ExpireAsync(task.Id, cancellationToken);
            if (expirationResult.IsSuccess)
            {
                processedCount++;
                continue;
            }

            firstError ??= expirationResult.Error;
        }

        return firstError is null
            ? Result<int>.Success(processedCount)
            : Result<int>.Failure(firstError);
    }

    public TimeSpan? GetNextActiveRemainingTime()
    {
        long nowTimestamp = _clock.GetTimestamp();
        TimeSpan[] remaining = _taskManager.GetAll()
            .Where(task => task.Status == LockTaskStatus.Active && task.StartedTimestamp is not null)
            .Select(task => task.Duration.Value - _clock.GetElapsedTime(
                task.StartedTimestamp!.Value,
                nowTimestamp))
            .Order()
            .ToArray();
        return remaining.Length == 0
            ? null
            : remaining[0] <= TimeSpan.Zero
                ? TimeSpan.Zero
                : remaining[0];
    }

    public async ValueTask<Result<int>> ProcessAdministrativeCleanupAsync(
        CancellationToken cancellationToken = default) =>
        (await ProcessAdministrativeCleanupWithDiagnosticsAsync(cancellationToken)).Result;

    public async ValueTask<AdministrativeCleanupReport>
        ProcessAdministrativeCleanupWithDiagnosticsAsync(
            CancellationToken cancellationToken = default)
    {
        FolderLockTask[] tasks = _taskManager.GetAll()
            .Where(task => task.Status is LockTaskStatus.Active or LockTaskStatus.UnlockFailed)
            .OrderBy(task => task.StartedTimestamp!.Value)
            .ThenBy(task => task.Id.Value)
            .ToArray();

        int successCount = 0;
        int errorCount = 0;
        bool recoveryRequired = false;
        Error? firstError = null;
        var failures = new List<AdministrativeCleanupFailure>();
        foreach (FolderLockTask task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result cleanupResult = await CleanupAdministrativelyAsync(task.Id, cancellationToken);
            if (cleanupResult.IsSuccess)
            {
                successCount++;
                continue;
            }

            errorCount++;
            bool isFirstError = firstError is null;
            firstError ??= cleanupResult.Error;
            bool taskRecoveryRequired = _taskManager.GetById(task.Id).Value.Status
                == LockTaskStatus.RecoveryRequired;
            recoveryRequired |= taskRecoveryRequired;
            failures.Add(new AdministrativeCleanupFailure(
                task.Id.Value,
                cleanupResult.Error!.Code,
                isFirstError,
                taskRecoveryRequired));
            _logger.LogWarning(
                "Administrative cleanup task {TaskId} failed with error {ErrorCode}. IsFirstError: {IsFirstError}. RecoveryRequired: {RecoveryRequired}.",
                task.Id.Value,
                cleanupResult.Error!.Code,
                isFirstError,
                taskRecoveryRequired);
        }

        _logger.LogInformation(
            "Administrative cleanup completed. FullyTraversed: {FullyTraversed}. SuccessCount: {SuccessCount}. ErrorCount: {ErrorCount}. RecoveryRequired: {RecoveryRequired}.",
            true,
            successCount,
            errorCount,
            recoveryRequired);

        Result<int> result = firstError is null
            ? Result<int>.Success(successCount)
            : Result<int>.Failure(firstError);
        return new AdministrativeCleanupReport(
            result,
            failures.AsReadOnly(),
            true,
            recoveryRequired);
    }

    private async ValueTask<Result> ExpireAsync(
        FolderLockTaskId taskId,
        CancellationToken cancellationToken)
    {
        Result<LockTaskTransition> unlocking = _taskManager.TryTransition(
            taskId,
            LockTaskStatus.Unlocking,
            _clock.UtcNow,
            removalIntent: LockRemovalIntent.Expiration);
        if (unlocking.IsFailure)
        {
            Result<FolderLockTask> current = _taskManager.GetById(taskId);
            if (current.IsSuccess && current.Value.Status != LockTaskStatus.Active)
            {
                return Result.Success();
            }

            return Result.Failure(unlocking.Error!);
        }

        if (unlocking.Value.Outcome == LockTaskTransitionOutcome.NoChange)
        {
            return Result.Success();
        }

        Result removeResult;
        try
        {
            removeResult = await _folderLockService.RemoveLockAsync(
                taskId.Value,
                LockRemovalIntent.Expiration,
                cancellationToken);
        }
        catch (Exception exception)
        {
            var error = new Error(
                "lock_task.expiration.exception",
                "The expiration removal ended without a confirmed result.",
                ErrorCategory.UnrecoverableError);
            Result<LockTaskTransition> failureTransition =
                TransitionToFailure(taskId, LockTaskStatus.RecoveryRequired, error);
            if (failureTransition.IsFailure)
            {
                return Result.Failure(failureTransition.Error!);
            }
            _logger.LogError(
                exception,
                "Task {TaskId} entered {TaskStatus} with error {ErrorCode}.",
                taskId.Value,
                LockTaskStatus.RecoveryRequired,
                error.Code);
            return Result.Failure(error);
        }

        if (removeResult.IsFailure)
        {
            LockTaskStatus failureStatus = removeResult.Error!.Category == ErrorCategory.UnrecoverableError
                ? LockTaskStatus.RecoveryRequired
                : LockTaskStatus.UnlockFailed;
            Result<LockTaskTransition> failureTransition =
                TransitionToFailure(taskId, failureStatus, removeResult.Error);
            if (failureTransition.IsFailure)
            {
                return Result.Failure(failureTransition.Error!);
            }
            _logger.LogWarning(
                "Task {TaskId} entered {TaskStatus} with error {ErrorCode}.",
                taskId.Value,
                failureStatus,
                removeResult.Error!.Code);
            return Result.Failure(removeResult.Error);
        }

        Result<LockTaskTransition> completed = _taskManager.TryTransition(
            taskId,
            LockTaskStatus.Completed,
            _clock.UtcNow);
        if (completed.IsSuccess)
        {
            return Result.Success();
        }

        var stateError = new Error(
            "lock_task.expiration.state_update_failed",
            "The lock was removed but its completed state could not be recorded.",
            ErrorCategory.UnrecoverableError);
        Result<LockTaskTransition> recoveryTransition =
            TransitionToFailure(taskId, LockTaskStatus.RecoveryRequired, stateError);
        return recoveryTransition.IsSuccess
            ? Result.Failure(stateError)
            : Result.Failure(recoveryTransition.Error!);
    }

    private async ValueTask<Result> CleanupAdministrativelyAsync(
        FolderLockTaskId taskId,
        CancellationToken cancellationToken)
    {
        Result<LockTaskTransition> unlocking = _taskManager.TryTransition(
            taskId,
            LockTaskStatus.Unlocking,
            _clock.UtcNow,
            removalIntent: LockRemovalIntent.AdministrativeCleanup);
        if (unlocking.IsFailure)
        {
            Result<FolderLockTask> current = _taskManager.GetById(taskId);
            if (current.IsSuccess
                && current.Value.Status is not LockTaskStatus.Active and not LockTaskStatus.UnlockFailed)
            {
                return Result.Success();
            }

            return Result.Failure(unlocking.Error!);
        }

        if (unlocking.Value.Outcome == LockTaskTransitionOutcome.NoChange)
        {
            return Result.Success();
        }

        Result removeResult;
        try
        {
            removeResult = await _folderLockService.RemoveLockAsync(
                taskId.Value,
                LockRemovalIntent.AdministrativeCleanup,
                cancellationToken);
        }
        catch (Exception exception)
        {
            var exceptionError = new Error(
                "lock_task.administrative_cleanup.exception",
                "The administrative cleanup ended without a confirmed result.",
                ErrorCategory.UnrecoverableError);
            Result<LockTaskTransition> failureTransition = TransitionToFailure(
                taskId,
                LockTaskStatus.RecoveryRequired,
                exceptionError,
                unlocking.Value.Task.StatusChangedAtUtc);
            _logger.LogError(
                "Administrative cleanup task {TaskId} raised exception type {ExceptionType}.",
                taskId.Value,
                exception.GetType().Name);
            return failureTransition.IsSuccess
                ? Result.Failure(exceptionError)
                : Result.Failure(failureTransition.Error!);
        }

        if (removeResult.IsFailure)
        {
            LockTaskStatus failureStatus = removeResult.Error!.Category == ErrorCategory.UnrecoverableError
                ? LockTaskStatus.RecoveryRequired
                : LockTaskStatus.UnlockFailed;
            Result<LockTaskTransition> failureTransition =
                TransitionToFailure(taskId, failureStatus, removeResult.Error);
            return failureTransition.IsSuccess
                ? Result.Failure(removeResult.Error)
                : Result.Failure(failureTransition.Error!);
        }

        DateTimeOffset completedAtUtc;
        Result<LockTaskTransition> completed;
        try
        {
            completedAtUtc = _clock.UtcNow;
            completed = _taskManager.TryTransition(
                taskId,
                LockTaskStatus.Completed,
                completedAtUtc);
        }
        catch (Exception exception)
        {
            var stateError = new Error(
                "lock_task.administrative_cleanup.state_update_failed",
                "The lock was removed but its completed state could not be recorded.",
                ErrorCategory.UnrecoverableError);
            Result<LockTaskTransition> recoveryTransition = TransitionToFailure(
                taskId,
                LockTaskStatus.RecoveryRequired,
                stateError,
                unlocking.Value.Task.StatusChangedAtUtc);
            _logger.LogError(
                "Administrative cleanup task {TaskId} state update raised exception type {ExceptionType}.",
                taskId.Value,
                exception.GetType().Name);
            return recoveryTransition.IsSuccess
                ? Result.Failure(stateError)
                : Result.Failure(recoveryTransition.Error!);
        }

        if (completed.IsSuccess)
        {
            return Result.Success();
        }

        var stateUpdateError = new Error(
            "lock_task.administrative_cleanup.state_update_failed",
            "The lock was removed but its completed state could not be recorded.",
            ErrorCategory.UnrecoverableError);
        Result<LockTaskTransition> transition = TransitionToFailure(
            taskId,
            LockTaskStatus.RecoveryRequired,
            stateUpdateError,
            completedAtUtc);
        return transition.IsSuccess
            ? Result.Failure(stateUpdateError)
            : Result.Failure(transition.Error!);
    }

    private Result<LockTaskTransition> TransitionToFailure(
        FolderLockTaskId taskId,
        LockTaskStatus targetStatus,
        Error error,
        DateTimeOffset? occurredAtUtc = null)
    {
        var taskError = new LockTaskError(error, occurredAtUtc ?? _clock.UtcNow);
        return _taskManager.TryTransition(
            taskId,
            targetStatus,
            taskError.OccurredAtUtc,
            taskError);
    }
}
