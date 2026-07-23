using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;

namespace FolderSessionLock.Core.Models;

public sealed record FolderLockTask
{
    private FolderLockTask(
        FolderLockTaskId id,
        FolderPath folderPath,
        LockDuration duration,
        LockTaskStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset statusChangedAtUtc,
        DateTimeOffset? startedAtUtc,
        long? startedTimestamp,
        DateTimeOffset? expectedExpiryUtc,
        LockTaskError? error)
    {
        Id = id;
        FolderPath = folderPath;
        Duration = duration;
        Status = status;
        CreatedAtUtc = createdAtUtc;
        StatusChangedAtUtc = statusChangedAtUtc;
        StartedAtUtc = startedAtUtc;
        StartedTimestamp = startedTimestamp;
        ExpectedExpiryUtc = expectedExpiryUtc;
        Error = error;
    }

    public FolderLockTaskId Id { get; }

    public FolderPath FolderPath { get; }

    public LockDuration Duration { get; }

    public LockTaskStatus Status { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset StatusChangedAtUtc { get; }

    public DateTimeOffset? StartedAtUtc { get; }

    public long? StartedTimestamp { get; }

    public DateTimeOffset? ExpectedExpiryUtc { get; }

    public LockTaskError? Error { get; }

    public TimeSpan GetRemainingTime(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (StartedTimestamp is null)
        {
            return Duration.Value;
        }

        TimeSpan elapsed = clock.GetElapsedTime(StartedTimestamp.Value, clock.GetTimestamp());
        TimeSpan remaining = Duration.Value - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public static Result<FolderLockTask> Create(
        FolderLockTaskId id,
        FolderPath folderPath,
        LockDuration duration,
        DateTimeOffset createdAtUtc)
    {
        if (!id.IsValid)
        {
            return Result<FolderLockTask>.Failure(new Error(
                "lock_task.id.invalid",
                "The folder lock task ID is invalid.",
                ErrorCategory.ValidationFailed));
        }

        if (!folderPath.IsValid)
        {
            return Result<FolderLockTask>.Failure(new Error(
                "lock_task.path.invalid",
                "The folder lock task path is invalid.",
                ErrorCategory.ValidationFailed));
        }

        if (!duration.IsValid)
        {
            return Result<FolderLockTask>.Failure(new Error(
                "lock_task.duration.invalid",
                "The folder lock task duration is invalid.",
                ErrorCategory.ValidationFailed));
        }

        DateTimeOffset createdUtc = createdAtUtc.ToUniversalTime();
        return Result<FolderLockTask>.Success(new FolderLockTask(
            id,
            folderPath,
            duration,
            LockTaskStatus.Created,
            createdUtc,
            createdUtc,
            null,
            null,
            null,
            null));
    }

    internal Result<LockTaskTransition> TryTransition(
        LockTaskStatus targetStatus,
        DateTimeOffset transitionedAtUtc,
        LockTaskError? error,
        long? startedTimestamp,
        LockRemovalIntent? removalIntent)
    {
        Result<LockTaskStatusTransition> statusTransition =
            LockTaskStateMachine.TryTransition(Status, targetStatus, removalIntent);
        if (statusTransition.IsFailure)
        {
            return Result<LockTaskTransition>.Failure(statusTransition.Error!);
        }

        if (statusTransition.Value.Outcome == LockTaskTransitionOutcome.NoChange)
        {
            return Result<LockTaskTransition>.Success(new LockTaskTransition(
                this,
                LockTaskTransitionOutcome.NoChange));
        }

        if (RequiresError(targetStatus) && error is null)
        {
            return Result<LockTaskTransition>.Failure(new Error(
                "lock_task.transition.error_required",
                $"Transition to {targetStatus} requires an error.",
                ErrorCategory.ValidationFailed));
        }

        if (targetStatus == LockTaskStatus.Active && startedTimestamp is null)
        {
            return Result<LockTaskTransition>.Failure(new Error(
                "lock_task.transition.timestamp_required",
                "Transition to Active requires a monotonic start timestamp.",
                ErrorCategory.ValidationFailed));
        }

        DateTimeOffset changedUtc = transitionedAtUtc.ToUniversalTime();
        DateTimeOffset? nextStartedAtUtc = StartedAtUtc;
        long? nextStartedTimestamp = StartedTimestamp;
        DateTimeOffset? nextExpectedExpiryUtc = ExpectedExpiryUtc;

        if (targetStatus == LockTaskStatus.Active)
        {
            nextStartedAtUtc = changedUtc;
            nextStartedTimestamp = startedTimestamp;
            try
            {
                nextExpectedExpiryUtc = changedUtc + Duration.Value;
            }
            catch (ArgumentOutOfRangeException)
            {
                return Result<LockTaskTransition>.Failure(new Error(
                    "lock_task.expiry.out_of_range",
                    "The expected UTC expiry cannot be represented.",
                    ErrorCategory.UnrecoverableError));
            }
        }

        LockTaskError? nextError = RequiresError(targetStatus) ? error : null;
        var updatedTask = new FolderLockTask(
            Id,
            FolderPath,
            Duration,
            targetStatus,
            CreatedAtUtc,
            changedUtc,
            nextStartedAtUtc,
            nextStartedTimestamp,
            nextExpectedExpiryUtc,
            nextError);

        return Result<LockTaskTransition>.Success(new LockTaskTransition(
            updatedTask,
            LockTaskTransitionOutcome.Applied));
    }

    private static bool RequiresError(LockTaskStatus status) =>
        status is LockTaskStatus.ActivationFailed
            or LockTaskStatus.UnlockFailed
            or LockTaskStatus.RecoveryRequired;
}
