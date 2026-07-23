using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Models;

public readonly record struct LockDuration
{
    private LockDuration(TimeSpan value)
    {
        Value = value;
    }

    public TimeSpan Value { get; }

    public bool IsValid => Value > TimeSpan.Zero;

    public static Result<LockDuration> Create(TimeSpan value, LockDurationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (value < policy.Minimum)
        {
            return Result<LockDuration>.Failure(new Error(
                "lock_duration.below_minimum",
                $"The lock duration must be at least {policy.Minimum}.",
                ErrorCategory.ValidationFailed));
        }

        if (value > policy.Maximum)
        {
            return Result<LockDuration>.Failure(new Error(
                "lock_duration.above_maximum",
                $"The lock duration must not exceed {policy.Maximum}.",
                ErrorCategory.ValidationFailed));
        }

        return Result<LockDuration>.Success(new LockDuration(value));
    }
}
