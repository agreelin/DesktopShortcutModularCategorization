using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.Broker.Lifecycle;

internal sealed class BrokerLifecycleController
{
    private readonly object _gate = new();
    private readonly ILockTaskScheduler _scheduler;
    private readonly LockTaskCoordinator _coordinator;
    private readonly ILogger<BrokerLifecycleController> _logger;
    private CancellationTokenSource? _schedulerCancellation;
    private Task<Result>? _schedulerTask;
    private Task<Result<int>>? _stopTask;

    internal BrokerLifecycleController(
        ILockTaskScheduler scheduler,
        LockTaskCoordinator coordinator,
        ILogger<BrokerLifecycleController> logger)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal Task<Result> RunSchedulerAsync()
    {
        lock (_gate)
        {
            if (_schedulerTask is not null)
            {
                return _schedulerTask;
            }

            _schedulerCancellation = new CancellationTokenSource();
            _schedulerTask = RunSchedulerCoreAsync(_schedulerCancellation.Token);
            return _schedulerTask;
        }
    }

    internal ValueTask<Result<int>> StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                return new ValueTask<Result<int>>(_stopTask);
            }

            _schedulerTask ??= Task.FromResult(Result.Success());
            _stopTask = StopCoreAsync(_schedulerTask, _schedulerCancellation);
            return new ValueTask<Result<int>>(_stopTask);
        }
    }

    internal async ValueTask<Result<int>> WaitForSessionEndingAndStopAsync(
        IBrokerSessionEndingSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await source.WaitAsync(cancellationToken);
        return await StopAsync();
    }

    private async Task<Result> RunSchedulerCoreAsync(CancellationToken cancellationToken) =>
        await _scheduler.RunAsync(cancellationToken);

    private async Task<Result<int>> StopCoreAsync(
        Task<Result> schedulerTask,
        CancellationTokenSource? schedulerCancellation)
    {
        schedulerCancellation?.Cancel();
        try
        {
            Result schedulerResult = await schedulerTask;
            if (schedulerResult.IsFailure
                && string.Equals(
                    schedulerResult.Error!.Code,
                    "lock_task.scheduler.loop.exception",
                    StringComparison.Ordinal))
            {
                LogProtected(
                    LogLevel.Error,
                    ProtectedLogEventCatalog.SchedulerStopped,
                    new ProtectedLogContext(ErrorCode: schedulerResult.Error!.Code));
            }
        }
        catch (Exception)
        {
            // The production LockTaskScheduler owns normalization of unexpected loop exceptions.
            // Lifecycle orchestration must not assign that contract to an exception thrown past it.
        }
        finally
        {
            schedulerCancellation?.Dispose();
        }

        AdministrativeCleanupReport cleanup =
            await _coordinator.ProcessAdministrativeCleanupWithDiagnosticsAsync();
        foreach (AdministrativeCleanupFailure failure in cleanup.Failures)
        {
            LogProtected(
                failure.RecoveryRequired ? LogLevel.Error : LogLevel.Warning,
                ProtectedLogEventCatalog.LifecycleCleanupTaskFailed,
                new ProtectedLogContext(
                    TaskId: failure.TaskId,
                    ErrorCode: failure.ErrorCode));
        }

        ProtectedLogEvent summaryEvent = cleanup.RecoveryRequired
            ? ProtectedLogEventCatalog.LifecycleCleanupRecoveryRequired
            : cleanup.Result.IsFailure
                ? ProtectedLogEventCatalog.LifecycleCleanupFailed
                : ProtectedLogEventCatalog.LifecycleCleanupCompleted;
        LogProtected(
            cleanup.RecoveryRequired
                ? LogLevel.Error
                : cleanup.Result.IsFailure
                    ? LogLevel.Warning
                    : LogLevel.Information,
            summaryEvent,
            new ProtectedLogContext(ErrorCode: cleanup.Result.Error?.Code));

        return cleanup.Result;
    }

    private void LogProtected(
        LogLevel level,
        ProtectedLogEvent entry,
        ProtectedLogContext context)
    {
        _logger.Log(
            level,
            new EventId(entry.EventId, entry.EventName),
            context,
            null,
            (_, _) => entry.Message);
    }
}
