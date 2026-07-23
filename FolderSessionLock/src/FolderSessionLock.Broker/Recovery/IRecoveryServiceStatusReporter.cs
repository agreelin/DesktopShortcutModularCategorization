namespace FolderSessionLock.Broker.Recovery;

internal interface IRecoveryServiceStatusReporter
{
    ValueTask ReportAsync(
        RecoveryServiceStatusSnapshot snapshot,
        CancellationToken cancellationToken);
}
