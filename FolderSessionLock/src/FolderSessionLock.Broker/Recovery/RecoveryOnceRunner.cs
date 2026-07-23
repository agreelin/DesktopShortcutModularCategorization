using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Recovery;

internal sealed class RecoveryOnceRunner
{
    private readonly Func<CancellationToken, ValueTask<RecoveryRunSummary>> _runBatch;

    internal RecoveryOnceRunner(RecoveryBatchRunner batchRunner)
        : this(batchRunner.RunAsync)
    {
    }

    internal RecoveryOnceRunner(Func<CancellationToken, ValueTask<RecoveryRunSummary>> runBatch)
    {
        _runBatch = runBatch ?? throw new ArgumentNullException(nameof(runBatch));
    }

    internal RecoveryRunSummary? LastSummary { get; private set; }

    internal async ValueTask<RecoveryOnceExitCode> RunAsync(
        bool argumentsValid,
        CancellationToken cancellationToken = default)
    {
        if (!argumentsValid)
        {
            return RecoveryOnceExitCode.InvalidArguments;
        }

        try
        {
            LastSummary = await _runBatch(cancellationToken);
            return Map(LastSummary);
        }
        catch (OperationCanceledException)
        {
            return RecoveryOnceExitCode.Cancelled;
        }
        catch (Exception)
        {
            return RecoveryOnceExitCode.InternalFailure;
        }
    }

    internal static RecoveryOnceExitCode Map(RecoveryRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string? error = summary.primaryErrorCode;
        if (error is not null && error.StartsWith("FSL_E_PROTECTED_PATH_", StringComparison.Ordinal))
        {
            return RecoveryOnceExitCode.ProtectedStorageSecurityFailure;
        }

        if (error is BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_OPEN_FAILED
            or BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_ENUMERATION_FAILED
            or BrokerErrorCodes.FSL_E_RECOVERY_ENTRY_METADATA_FAILED)
        {
            return RecoveryOnceExitCode.RecoveryEnumerationFailure;
        }

        if (error == BrokerErrorCodes.FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED)
        {
            return RecoveryOnceExitCode.RecoveryRecordLimitExceeded;
        }

        bool pureCancellation = summary.skippedCount > 0
            && summary.failedCount == 0
            && summary.recoveryRequiredCount == 0
            && summary.invalidArtifactCount == 0
            && error is null or BrokerErrorCodes.FSL_E_OPERATION_CANCELLED;
        if (summary.recoveryBlocking && !pureCancellation)
        {
            return RecoveryOnceExitCode.RecoveryBlocked;
        }

        if (pureCancellation)
        {
            return RecoveryOnceExitCode.Cancelled;
        }

        return summary.recoveryBlocking
            ? RecoveryOnceExitCode.InternalFailure
            : RecoveryOnceExitCode.Success;
    }
}
