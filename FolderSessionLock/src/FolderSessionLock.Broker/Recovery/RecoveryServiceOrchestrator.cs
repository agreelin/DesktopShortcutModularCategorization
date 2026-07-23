namespace FolderSessionLock.Broker.Recovery;

using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal sealed class RecoveryServiceOrchestrator
{
    private static readonly TimeSpan WaitHint = TimeSpan.FromSeconds(30);
    private readonly RecoveryBatchRunner _batchRunner;
    private readonly IRecoveryReadinessStore _readinessStore;
    private readonly IRecoveryServiceStatusReporter _statusReporter;
    private readonly FolderSessionLock.Core.Abstractions.IClock _clock;
    private readonly ILogger<RecoveryServiceOrchestrator> _logger;
    private readonly IProtectedLoggerHealth? _loggerHealth;
    private readonly IProtectedLogMaintenance? _loggerMaintenance;
    private readonly Guid _serviceInstanceId;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<RecoveryServiceState>? _startTask;
    private Task? _heartbeatTask;
    private Task? _maintenanceTask;
    private int _checkpoint;
    private RecoveryRunSummary? _summary;
    private DateTimeOffset _scanStartedUtc;
    private DateTimeOffset? _scanCompletedUtc;
    private long _sequence;
    private readonly TaskCompletionSource _stopRequested = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal RecoveryServiceOrchestrator(
        RecoveryBatchRunner batchRunner,
        IRecoveryReadinessStore readinessStore,
        IRecoveryServiceStatusReporter statusReporter,
        Guid? serviceInstanceId = null,
        FolderSessionLock.Core.Abstractions.IClock? clock = null,
        ILoggerFactory? loggerFactory = null)
    {
        _batchRunner = batchRunner ?? throw new ArgumentNullException(nameof(batchRunner));
        _readinessStore = readinessStore ?? throw new ArgumentNullException(nameof(readinessStore));
        _statusReporter = statusReporter ?? throw new ArgumentNullException(nameof(statusReporter));
        _clock = clock ?? new SystemClock();
        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger<RecoveryServiceOrchestrator>();
        _loggerHealth = loggerFactory as IProtectedLoggerHealth;
        _loggerMaintenance = loggerFactory as IProtectedLogMaintenance;
        _serviceInstanceId = serviceInstanceId ?? Guid.NewGuid();
        if (_serviceInstanceId == Guid.Empty)
        {
            throw new ArgumentException("The service instance ID must not be empty.", nameof(serviceInstanceId));
        }
    }

    internal Task<RecoveryServiceState> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _startTask ??= StartCoreAsync(cancellationToken);
            return _startTask;
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ReportAsync(RecoveryServiceState.Stopping, false, null, cancellationToken);
            await PublishAsync(
                RecoveryReadinessState.Stopping,
                true,
                _summary?.remainingRecordCount ?? -1,
                _summary?.primaryErrorCode,
                cancellationToken);
        }
        finally
        {
            _stop.Cancel();
            await WaitForCompletionAsync();
            if (_heartbeatTask is not null)
            {
                await _heartbeatTask;
            }

            if (_maintenanceTask is not null)
            {
                await _maintenanceTask;
            }
        }

        await PublishAsync(
            RecoveryReadinessState.Stopping,
            true,
            _summary?.remainingRecordCount ?? -1,
            _summary?.primaryErrorCode,
            CancellationToken.None);
        try
        {
            await _readinessStore.DeleteAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }

        await ReportAsync(RecoveryServiceState.Stopped, false, _summary?.primaryErrorCode, cancellationToken);
    }

    internal async ValueTask WaitForCompletionAsync()
    {
        Task<RecoveryServiceState>? start;
        lock (_sync)
        {
            start = _startTask;
        }

        if (start is not null)
        {
            await start;
        }
    }

    internal Task WaitForStopRequestAsync() => _stopRequested.Task;

    private async Task<RecoveryServiceState> StartCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stop.Token);
        _scanStartedUtc = _clock.UtcNow;
        try
        {
            await ReportAsync(RecoveryServiceState.StartPending, false, null, linked.Token);
            await PublishAsync(RecoveryReadinessState.Starting, true, -1, null, linked.Token);
            if (!LogReadinessState(null))
            {
                await PublishLoggerFailureAndStopAsync();
                return RecoveryServiceState.Stopped;
            }
            await ReportAsync(RecoveryServiceState.Preflight, false, null, linked.Token);
            await ReportAsync(RecoveryServiceState.Scanning, false, null, linked.Token);
            _summary = await _batchRunner.RunAsync(linked.Token);
            _scanCompletedUtc = _clock.UtcNow;

            if (_stop.IsCancellationRequested)
            {
                return RecoveryServiceState.Stopping;
            }

            if (IsStartupFailure(_summary.primaryErrorCode))
            {
                await ReportAsync(
                    RecoveryServiceState.Stopped,
                    false,
                    _summary.primaryErrorCode,
                    CancellationToken.None);
                return RecoveryServiceState.Stopped;
            }

            RecoveryServiceState state = _summary.recoveryBlocking
                ? RecoveryServiceState.RecoveryBlocked
                : RecoveryServiceState.Ready;
            await PublishAsync(
                state == RecoveryServiceState.Ready
                    ? RecoveryReadinessState.Ready
                    : RecoveryReadinessState.RecoveryBlocked,
                _summary.recoveryBlocking,
                _summary.remainingRecordCount,
                _summary.primaryErrorCode,
                CancellationToken.None);
            await ReportAsync(state, true, _summary.primaryErrorCode, CancellationToken.None);
            if (!LogReadinessState(_summary.primaryErrorCode))
            {
                await PublishLoggerFailureAndStopAsync();
                return RecoveryServiceState.Stopped;
            }
            _heartbeatTask = RunHeartbeatAsync(
                state == RecoveryServiceState.Ready
                    ? RecoveryReadinessState.Ready
                    : RecoveryReadinessState.RecoveryBlocked,
                _summary.recoveryBlocking,
                _summary.remainingRecordCount,
                _summary.primaryErrorCode);
            if (_loggerMaintenance is not null)
            {
                _maintenanceTask = RunMaintenanceAsync(
                    _summary.remainingRecordCount);
            }

            return state;
        }
        catch (Exception)
        {
            await ReportAsync(
                RecoveryServiceState.Stopped,
                false,
                _summary?.primaryErrorCode,
                CancellationToken.None);
            return RecoveryServiceState.Stopped;
        }
    }

    private ValueTask PublishAsync(
        RecoveryReadinessState state,
        bool blocking,
        int remaining,
        string? error,
        CancellationToken cancellationToken)
    {
        DateTimeOffset publishedUtc = _clock.UtcNow;
        return _readinessStore.PublishAsync(
            new RecoveryReadinessSnapshot(
                RecoveryReadinessPolicy.SchemaVersion,
                RecoveryReadinessPolicy.ServiceName,
                _serviceInstanceId,
                Interlocked.Increment(ref _sequence),
                state,
                blocking,
                _scanStartedUtc,
                state is RecoveryReadinessState.Ready or RecoveryReadinessState.RecoveryBlocked
                    ? _scanCompletedUtc ?? publishedUtc
                    : null,
                publishedUtc,
                publishedUtc + RecoveryReadinessPolicy.Validity,
                remaining,
                error),
            cancellationToken);
    }

    private async Task RunHeartbeatAsync(
        RecoveryReadinessState state,
        bool blocking,
        int remaining,
        string? error)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _clock.DelayAsync(RecoveryReadinessPolicy.HeartbeatInterval, _stop.Token);
                if (_stop.IsCancellationRequested)
                {
                    break;
                }

                await PublishAsync(state, blocking, remaining, error, _stop.Token);
                if (!LogReadinessState(error))
                {
                    await PublishAsync(
                        RecoveryReadinessState.RecoveryBlocked,
                        true,
                        remaining,
                        BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
                        CancellationToken.None);
                    RequestStop();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            RequestStop();
        }
    }

    private bool LogReadinessState(string? errorCode)
    {
        ProtectedLogEvent entry = ProtectedLogEventCatalog.ReadinessStateChanged;
        _logger.Log(
            LogLevel.Information,
            new EventId(entry.EventId, entry.EventName),
            new ProtectedLogContext(ErrorCode: errorCode),
            null,
            static (_, _) => ProtectedLogEventCatalog.ReadinessStateChanged.Message);
        return _loggerHealth is not { IsPermanentlyFailed: true };
    }

    private async Task RunMaintenanceAsync(int remaining)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _clock.DelayAsync(_loggerMaintenance!.MaintenanceInterval, _stop.Token);
                if (_stop.IsCancellationRequested)
                {
                    break;
                }

                Result maintenance = _loggerMaintenance.RunMaintenance();
                if (maintenance.IsFailure)
                {
                    await PublishAsync(
                        RecoveryReadinessState.RecoveryBlocked,
                        true,
                        remaining,
                        BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
                        CancellationToken.None);
                    RequestStop();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await PublishAsync(
                RecoveryReadinessState.RecoveryBlocked,
                true,
                remaining,
                BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
                CancellationToken.None);
            RequestStop();
        }
    }

    private async Task PublishLoggerFailureAndStopAsync()
    {
        await PublishAsync(
            RecoveryReadinessState.RecoveryBlocked,
            true,
            _summary?.remainingRecordCount ?? -1,
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            CancellationToken.None);
        await ReportAsync(
            RecoveryServiceState.Stopped,
            false,
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            CancellationToken.None);
        RequestStop();
    }

    private void RequestStop()
    {
        _stop.Cancel();
        _stopRequested.TrySetResult();
    }

    private ValueTask ReportAsync(
        RecoveryServiceState state,
        bool running,
        string? error,
        CancellationToken cancellationToken) => _statusReporter.ReportAsync(
            new RecoveryServiceStatusSnapshot(
                state,
                running,
                Interlocked.Increment(ref _checkpoint),
                WaitHint,
                error),
            cancellationToken);

    private static bool IsStartupFailure(string? errorCode) =>
        errorCode is not null
        && (errorCode.StartsWith("FSL_E_PROTECTED_PATH_", StringComparison.Ordinal)
            || errorCode is BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_OPEN_FAILED
                or BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_ENUMERATION_FAILED
                or BrokerErrorCodes.FSL_E_RECOVERY_ENTRY_METADATA_FAILED);
}

internal sealed class UnavailableRecoveryReadinessPublisher : IRecoveryReadinessStore
{
    public ValueTask PublishAsync(
        RecoveryReadinessSnapshot snapshot,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new InvalidOperationException("Recovery readiness storage is unavailable."));

    public ValueTask<RecoveryReadinessSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<RecoveryReadinessSnapshot>(
            new InvalidOperationException("Recovery readiness storage is unavailable."));

    public ValueTask DeleteAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException(new InvalidOperationException("Recovery readiness storage is unavailable."));
}

internal sealed class RecoveryOnceStatusReporter : IRecoveryServiceStatusReporter
{
    public ValueTask ReportAsync(
        RecoveryServiceStatusSnapshot snapshot,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
