namespace FolderSessionLock.Broker.Recovery;

internal sealed class RecoveryRunSummary
{
    internal RecoveryRunSummary(
        int canonicalRecordCount,
        int processedRecordCount,
        int cleanedCount,
        int alreadyCleanCount,
        int failedCount,
        int recoveryRequiredCount,
        int skippedCount,
        int auxiliaryArtifactCount,
        int invalidArtifactCount,
        int remainingRecordCount,
        bool recoveryBlocking,
        string? primaryErrorCode)
    {
        int[] counts =
        [
            canonicalRecordCount,
            processedRecordCount,
            cleanedCount,
            alreadyCleanCount,
            failedCount,
            recoveryRequiredCount,
            skippedCount,
            auxiliaryArtifactCount,
            invalidArtifactCount,
            remainingRecordCount,
        ];
        if (counts.Any(count => count is < 0 or > 4096))
        {
            throw new ArgumentOutOfRangeException(nameof(canonicalRecordCount));
        }

        if (processedRecordCount != cleanedCount + alreadyCleanCount + failedCount + recoveryRequiredCount)
        {
            throw new ArgumentException("The processed recovery record count is inconsistent.");
        }

        if (canonicalRecordCount != processedRecordCount + skippedCount)
        {
            throw new ArgumentException("The canonical recovery record count is inconsistent.");
        }

        if (!recoveryBlocking
            && (failedCount > 0
                || recoveryRequiredCount > 0
                || skippedCount > 0
                || invalidArtifactCount > 0
                || remainingRecordCount > 0
                || primaryErrorCode is not null))
        {
            throw new ArgumentException("A non-blocking recovery summary contains blocking evidence.");
        }

        this.canonicalRecordCount = canonicalRecordCount;
        this.processedRecordCount = processedRecordCount;
        this.cleanedCount = cleanedCount;
        this.alreadyCleanCount = alreadyCleanCount;
        this.failedCount = failedCount;
        this.recoveryRequiredCount = recoveryRequiredCount;
        this.skippedCount = skippedCount;
        this.auxiliaryArtifactCount = auxiliaryArtifactCount;
        this.invalidArtifactCount = invalidArtifactCount;
        this.remainingRecordCount = remainingRecordCount;
        this.recoveryBlocking = recoveryBlocking;
        this.primaryErrorCode = primaryErrorCode;
    }

    public int canonicalRecordCount { get; }
    public int processedRecordCount { get; }
    public int cleanedCount { get; }
    public int alreadyCleanCount { get; }
    public int failedCount { get; }
    public int recoveryRequiredCount { get; }
    public int skippedCount { get; }
    public int auxiliaryArtifactCount { get; }
    public int invalidArtifactCount { get; }
    public int remainingRecordCount { get; }
    public bool recoveryBlocking { get; }
    public string? primaryErrorCode { get; }
}
