using FolderSessionLock.Protocol;

namespace FolderSessionLock.Core.Recovery;

public static class RecoveryReadinessPolicy
{
    public const int SchemaVersion = 1;
    public const string ServiceName = "FolderSessionLockRecovery";
    public const string CanonicalLeafName = "recovery-readiness.v1.json";
    public const int MaximumLength = 16_384;
    public const int MaximumRemainingRecordCount = 1_024;
    public const int MaximumPrimaryErrorCodeLength = 128;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Validity = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(5);

    public static bool IsReady(
        RecoveryReadinessSnapshot? snapshot,
        DateTimeOffset nowUtc)
    {
        if (snapshot is null)
        {
            return false;
        }

        return Validate(snapshot, nowUtc) is null
            && snapshot.State == RecoveryReadinessState.Ready;
    }

    public static string? Validate(
        RecoveryReadinessSnapshot snapshot,
        DateTimeOffset nowUtc,
        RecoveryReadinessSnapshot? previousSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != SchemaVersion)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_READINESS_VERSION_UNSUPPORTED;
        }

        if (!string.Equals(snapshot.ServiceName, ServiceName, StringComparison.Ordinal)
            || snapshot.ServiceInstanceId == Guid.Empty
            || snapshot.Sequence < 1
            || snapshot.RemainingRecordCount is < -1 or > MaximumRemainingRecordCount
            || (snapshot.PrimaryErrorCode is not null
                && (snapshot.PrimaryErrorCode.Length > MaximumPrimaryErrorCodeLength
                    || !BrokerProtocolValidation.IsErrorCode(snapshot.PrimaryErrorCode)))
            || snapshot.ScanStartedUtc.Offset != TimeSpan.Zero
            || (snapshot.ScanCompletedUtc is { } completed
                && completed.Offset != TimeSpan.Zero)
            || snapshot.PublishedUtc.Offset != TimeSpan.Zero
            || snapshot.ValidUntilUtc.Offset != TimeSpan.Zero
            || snapshot.ValidUntilUtc != snapshot.PublishedUtc + Validity)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID;
        }

        bool matrixValid = snapshot.State switch
        {
            RecoveryReadinessState.Starting => snapshot.RecoveryBlocking
                && snapshot.ScanCompletedUtc is null
                && snapshot.RemainingRecordCount == -1
                && snapshot.PrimaryErrorCode is null,
            RecoveryReadinessState.Ready => !snapshot.RecoveryBlocking
                && snapshot.ScanCompletedUtc is not null
                && snapshot.RemainingRecordCount == 0
                && snapshot.PrimaryErrorCode is null,
            RecoveryReadinessState.RecoveryBlocked => snapshot.RecoveryBlocking
                && snapshot.ScanCompletedUtc is not null
                && snapshot.RemainingRecordCount is >= 0 and <= MaximumRemainingRecordCount
                && snapshot.PrimaryErrorCode is not null,
            RecoveryReadinessState.Stopping => snapshot.RecoveryBlocking
                && (snapshot.RemainingRecordCount == -1
                    || snapshot.RemainingRecordCount is >= 0 and <= MaximumRemainingRecordCount),
            _ => false,
        };
        if (!matrixValid)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID;
        }

        if (previousSnapshot is not null
            && previousSnapshot.ServiceInstanceId == snapshot.ServiceInstanceId
            && snapshot.Sequence <= previousSnapshot.Sequence)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID;
        }

        if (snapshot.PublishedUtc > nowUtc + FutureTolerance
            || nowUtc > snapshot.ValidUntilUtc)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_READINESS_STALE;
        }

        return null;
    }
}
