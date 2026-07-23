using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Services;

public static class LockTaskStateMachine
{
    public static Result<LockTaskStatusTransition> TryTransition(
        LockTaskStatus currentStatus,
        LockTaskStatus targetStatus,
        LockRemovalIntent? removalIntent = null)
    {
        if (currentStatus == targetStatus)
        {
            return Result<LockTaskStatusTransition>.Success(new LockTaskStatusTransition(
                currentStatus,
                currentStatus,
                LockTaskTransitionOutcome.NoChange));
        }

        if (!IsAllowed(currentStatus, targetStatus, removalIntent))
        {
            return Result<LockTaskStatusTransition>.Failure(new Error(
                "lock_task.transition.invalid",
                $"Transition from {currentStatus} to {targetStatus} is not allowed.",
                ErrorCategory.ValidationFailed));
        }

        return Result<LockTaskStatusTransition>.Success(new LockTaskStatusTransition(
            currentStatus,
            targetStatus,
            LockTaskTransitionOutcome.Applied));
    }

    private static bool IsAllowed(
        LockTaskStatus currentStatus,
        LockTaskStatus targetStatus,
        LockRemovalIntent? removalIntent) => (currentStatus, targetStatus) switch
        {
            (LockTaskStatus.Created, LockTaskStatus.Activating) => true,
            (LockTaskStatus.Activating, LockTaskStatus.Active) => true,
            (LockTaskStatus.Activating, LockTaskStatus.ActivationFailed) => true,
            (LockTaskStatus.Activating, LockTaskStatus.RecoveryRequired) => true,
            (LockTaskStatus.Active, LockTaskStatus.Unlocking) =>
                removalIntent is LockRemovalIntent.Expiration
                    or LockRemovalIntent.AdministrativeCleanup,
            (LockTaskStatus.Unlocking, LockTaskStatus.Completed) => true,
            (LockTaskStatus.Unlocking, LockTaskStatus.UnlockFailed) => true,
            (LockTaskStatus.Unlocking, LockTaskStatus.RecoveryRequired) => true,
            (LockTaskStatus.ActivationFailed, LockTaskStatus.RecoveryRequired) => true,
            (LockTaskStatus.UnlockFailed, LockTaskStatus.Unlocking) =>
                removalIntent is LockRemovalIntent.Recovery
                    or LockRemovalIntent.TestCleanup
                    or LockRemovalIntent.AdministrativeCleanup,
            (LockTaskStatus.UnlockFailed, LockTaskStatus.RecoveryRequired) => true,
            _ => false,
        };
}
