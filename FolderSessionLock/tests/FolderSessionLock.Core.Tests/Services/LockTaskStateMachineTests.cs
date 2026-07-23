using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;

namespace FolderSessionLock.Core.Tests.Services;

public sealed class LockTaskStateMachineTests
{
    public static TheoryData<LockTaskStatus, LockTaskStatus, LockRemovalIntent?> AllowedTransitions => new()
    {
        { LockTaskStatus.Created, LockTaskStatus.Activating, null },
        { LockTaskStatus.Activating, LockTaskStatus.Active, null },
        { LockTaskStatus.Activating, LockTaskStatus.ActivationFailed, null },
        { LockTaskStatus.Activating, LockTaskStatus.RecoveryRequired, null },
        { LockTaskStatus.Active, LockTaskStatus.Unlocking, LockRemovalIntent.Expiration },
        { LockTaskStatus.Active, LockTaskStatus.Unlocking, LockRemovalIntent.AdministrativeCleanup },
        { LockTaskStatus.Unlocking, LockTaskStatus.Completed, null },
        { LockTaskStatus.Unlocking, LockTaskStatus.UnlockFailed, null },
        { LockTaskStatus.Unlocking, LockTaskStatus.RecoveryRequired, null },
        { LockTaskStatus.ActivationFailed, LockTaskStatus.RecoveryRequired, null },
        { LockTaskStatus.UnlockFailed, LockTaskStatus.Unlocking, LockRemovalIntent.Recovery },
        { LockTaskStatus.UnlockFailed, LockTaskStatus.Unlocking, LockRemovalIntent.TestCleanup },
        { LockTaskStatus.UnlockFailed, LockTaskStatus.Unlocking, LockRemovalIntent.AdministrativeCleanup },
        { LockTaskStatus.UnlockFailed, LockTaskStatus.RecoveryRequired, null },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void TryTransition_AllowsDefinedTransition(
        LockTaskStatus current,
        LockTaskStatus target,
        LockRemovalIntent? intent)
    {
        var result = LockTaskStateMachine.TryTransition(current, target, intent);

        Assert.True(result.IsSuccess);
        Assert.Equal(LockTaskTransitionOutcome.Applied, result.Value.Outcome);
        Assert.Equal(current, result.Value.PreviousStatus);
        Assert.Equal(target, result.Value.CurrentStatus);
    }

    [Theory]
    [InlineData(LockTaskStatus.Created)]
    [InlineData(LockTaskStatus.Activating)]
    [InlineData(LockTaskStatus.Active)]
    [InlineData(LockTaskStatus.Unlocking)]
    [InlineData(LockTaskStatus.Completed)]
    [InlineData(LockTaskStatus.ActivationFailed)]
    [InlineData(LockTaskStatus.UnlockFailed)]
    [InlineData(LockTaskStatus.RecoveryRequired)]
    public void TryTransition_SameStatus_ReturnsNoChange(LockTaskStatus status)
    {
        var result = LockTaskStateMachine.TryTransition(status, status);

        Assert.True(result.IsSuccess);
        Assert.Equal(LockTaskTransitionOutcome.NoChange, result.Value.Outcome);
        Assert.Equal(status, result.Value.CurrentStatus);
    }

    [Theory]
    [InlineData(LockTaskStatus.Created, LockTaskStatus.Active)]
    [InlineData(LockTaskStatus.Active, LockTaskStatus.Completed)]
    [InlineData(LockTaskStatus.Completed, LockTaskStatus.Active)]
    [InlineData(LockTaskStatus.RecoveryRequired, LockTaskStatus.Unlocking)]
    public void TryTransition_RejectsUndefinedTransition(
        LockTaskStatus current,
        LockTaskStatus target)
    {
        var result = LockTaskStateMachine.TryTransition(current, target);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.ValidationFailed, result.Error!.Category);
        Assert.Equal("lock_task.transition.invalid", result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LockRemovalIntent.Recovery)]
    [InlineData(LockRemovalIntent.TestCleanup)]
    public void TryTransition_ActiveToUnlocking_RejectsUnsupportedIntent(LockRemovalIntent? intent)
    {
        Assert.True(LockTaskStateMachine.TryTransition(
            LockTaskStatus.Active,
            LockTaskStatus.Unlocking,
            intent).IsFailure);
    }

    [Fact]
    public void TryTransition_UnlockFailedToUnlocking_RejectsExpiration()
    {
        Assert.True(LockTaskStateMachine.TryTransition(
            LockTaskStatus.UnlockFailed,
            LockTaskStatus.Unlocking,
            LockRemovalIntent.Expiration).IsFailure);
    }

    [Fact]
    public void TryTransition_EveryUnlistedTransitionWithoutIntent_IsRejected()
    {
        var allowedWithoutIntent = new HashSet<(LockTaskStatus Current, LockTaskStatus Target)>
        {
            (LockTaskStatus.Created, LockTaskStatus.Activating),
            (LockTaskStatus.Activating, LockTaskStatus.Active),
            (LockTaskStatus.Activating, LockTaskStatus.ActivationFailed),
            (LockTaskStatus.Activating, LockTaskStatus.RecoveryRequired),
            (LockTaskStatus.Unlocking, LockTaskStatus.Completed),
            (LockTaskStatus.Unlocking, LockTaskStatus.UnlockFailed),
            (LockTaskStatus.Unlocking, LockTaskStatus.RecoveryRequired),
            (LockTaskStatus.ActivationFailed, LockTaskStatus.RecoveryRequired),
            (LockTaskStatus.UnlockFailed, LockTaskStatus.RecoveryRequired),
        };

        foreach (LockTaskStatus current in Enum.GetValues<LockTaskStatus>())
        {
            foreach (LockTaskStatus target in Enum.GetValues<LockTaskStatus>())
            {
                var result = LockTaskStateMachine.TryTransition(current, target);
                if (current == target || allowedWithoutIntent.Contains((current, target)))
                {
                    Assert.True(result.IsSuccess, $"Expected {current} -> {target} to succeed.");
                }
                else
                {
                    Assert.True(result.IsFailure, $"Expected {current} -> {target} to fail.");
                }
            }
        }
    }
}
