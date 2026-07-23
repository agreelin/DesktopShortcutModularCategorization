using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Models;

public sealed record LockDurationPolicy
{
    public const long MinimumDurationMilliseconds = 60_000;
    public const long MaximumDurationMilliseconds = 86_400_000;
    private LockDurationPolicy(TimeSpan minimum, TimeSpan maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    public TimeSpan Minimum { get; }

    public TimeSpan Maximum { get; }

    public static Result<LockDurationPolicy> Create(TimeSpan minimum, TimeSpan maximum)
    {
        if (minimum <= TimeSpan.Zero)
        {
            return Result<LockDurationPolicy>.Failure(new Error(
                "lock_duration_policy.minimum.invalid",
                "The minimum lock duration must be greater than zero.",
                ErrorCategory.ValidationFailed));
        }

        if (maximum < minimum)
        {
            return Result<LockDurationPolicy>.Failure(new Error(
                "lock_duration_policy.range.invalid",
                "The maximum lock duration must be greater than or equal to the minimum.",
                ErrorCategory.ValidationFailed));
        }

        return Result<LockDurationPolicy>.Success(new LockDurationPolicy(minimum, maximum));
    }

    public static LockDurationPolicy CreateProduction() => new(
        TimeSpan.FromMilliseconds(MinimumDurationMilliseconds),
        TimeSpan.FromMilliseconds(MaximumDurationMilliseconds));
}
