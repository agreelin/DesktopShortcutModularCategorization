namespace FolderSessionLock.Broker.Recovery;

using FolderSessionLock.Protocol;

internal enum RecoveryRecordCleanupDisposition
{
    Cleaned,
    AlreadyClean,
    Failed,
    RecoveryRequired,
    Skipped,
}

internal sealed record RecoveryRecordCleanupResult(
    Guid RecordId,
    RecoveryRecordCleanupDisposition Disposition,
    string? ErrorCode)
{
    internal static RecoveryRecordCleanupResult Cleaned(Guid id) =>
        new(id, RecoveryRecordCleanupDisposition.Cleaned, null);

    internal static RecoveryRecordCleanupResult AlreadyClean(Guid id) =>
        new(id, RecoveryRecordCleanupDisposition.AlreadyClean, null);

    internal static RecoveryRecordCleanupResult Failed(Guid id, string code) =>
        new(id, RecoveryRecordCleanupDisposition.Failed, code);

    internal static RecoveryRecordCleanupResult RecoveryRequired(Guid id, string code) =>
        new(id, RecoveryRecordCleanupDisposition.RecoveryRequired, code);

    internal static RecoveryRecordCleanupResult Skipped(Guid id) =>
        new(id, RecoveryRecordCleanupDisposition.Skipped, BrokerErrorCodes.FSL_E_OPERATION_CANCELLED);
}
