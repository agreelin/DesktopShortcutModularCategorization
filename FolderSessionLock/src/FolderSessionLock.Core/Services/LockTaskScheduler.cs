using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Results;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.Core.Services;

public sealed class LockTaskScheduler : ILockTaskScheduler
{
    public static readonly TimeSpan MaximumDelaySegment = TimeSpan.FromSeconds(30);
    private readonly LockTaskCoordinator _coordinator;
    private readonly IClock _clock;
    private readonly ILogger<LockTaskScheduler> _logger;

    public LockTaskScheduler(
        LockTaskCoordinator coordinator,
        IClock clock,
        ILogger<LockTaskScheduler> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask<Result<int>> ProcessDueTasksAsync(
        CancellationToken cancellationToken = default) =>
        _coordinator.ProcessDueTasksAsync(cancellationToken);

    public async ValueTask<Result> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan? remaining = _coordinator.GetNextActiveRemainingTime();
                if (remaining is null)
                {
                    return Result.Success();
                }

                if (remaining <= TimeSpan.Zero)
                {
                    Result<int> result = await ProcessDueTasksAsync(cancellationToken);
                    if (result.IsFailure)
                    {
                        _logger.LogWarning(
                            "Scheduler expiration completed with error {ErrorCode}.",
                            result.Error!.Code);
                        return Result.Failure(result.Error!);
                    }

                    continue;
                }

                await _clock.DelayAsync(
                    remaining.Value < MaximumDelaySegment
                        ? remaining.Value
                        : MaximumDelaySegment,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result.Success();
        }
        catch (Exception)
        {
            var error = new Error(
                "lock_task.scheduler.loop.exception",
                "The lock task scheduler loop terminated unexpectedly.",
                ErrorCategory.PlatformError);
            _logger.LogError(
                "{Message} ErrorCode: {ErrorCode}.",
                error.Message,
                error.Code);
            return Result.Failure(error);
        }
    }
}
