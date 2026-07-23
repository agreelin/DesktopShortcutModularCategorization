using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Broker.Recovery;

internal class RecoveryBatchRunner
{
    private readonly IProtectedPathSecurityVerifier _securityVerifier;
    private readonly IReadOnlyList<ProtectedPathSecurityCheckRequest> _securityRequests;
    private readonly RecoveryDirectoryEnumerator _enumerator;
    private readonly RecoveryRecordAclCleanup _cleanup;

    internal RecoveryBatchRunner(
        IProtectedPathSecurityVerifier securityVerifier,
        IReadOnlyList<ProtectedPathSecurityCheckRequest> securityRequests,
        RecoveryDirectoryEnumerator enumerator,
        RecoveryRecordAclCleanup cleanup)
    {
        _securityVerifier = securityVerifier ?? throw new ArgumentNullException(nameof(securityVerifier));
        _securityRequests = securityRequests ?? throw new ArgumentNullException(nameof(securityRequests));
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
    }

    internal virtual async ValueTask<RecoveryRunSummary> RunAsync(
        CancellationToken cancellationToken = default)
    {
        foreach (ProtectedPathSecurityCheckRequest request in _securityRequests)
        {
            ProtectedPathSecurityCheckResult result;
            try
            {
                result = await _securityVerifier.VerifyAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Empty(BrokerErrorCodes.FSL_E_OPERATION_CANCELLED);
            }
            catch (Exception)
            {
                return Empty(BrokerErrorCodes.FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED);
            }

            if (result is null || !result.IsTrusted || result.ErrorCode is not null)
            {
                return Empty(result?.ErrorCode ?? BrokerErrorCodes.FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED);
            }
        }

        var snapshotResult = await _enumerator.EnumerateAsync(cancellationToken);
        if (snapshotResult.IsFailure)
        {
            return Empty(snapshotResult.Error!.Code);
        }

        RecoveryDirectorySnapshot snapshot = snapshotResult.Value;
        int cleaned = 0;
        int alreadyClean = 0;
        int failed = 0;
        int recoveryRequired = 0;
        int skipped = 0;
        string? recordError = null;
        foreach (RecoveryDirectoryRecord record in snapshot.CanonicalRecords)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                skipped++;
                continue;
            }

            var identityCheck = _enumerator.VerifyIdentity(snapshot.DirectoryIdentity);
            if (identityCheck.IsFailure)
            {
                skipped += snapshot.CanonicalRecords.Count
                    - cleaned
                    - alreadyClean
                    - failed
                    - recoveryRequired
                    - skipped;
                recordError ??= identityCheck.Error!.Code;
                break;
            }

            RecoveryRecordCleanupResult result = await _cleanup.ExecuteAsync(
                record,
                cancellationToken);
            switch (result.Disposition)
            {
                case RecoveryRecordCleanupDisposition.Cleaned:
                    cleaned++;
                    break;
                case RecoveryRecordCleanupDisposition.AlreadyClean:
                    alreadyClean++;
                    break;
                case RecoveryRecordCleanupDisposition.Failed:
                    failed++;
                    recordError ??= result.ErrorCode;
                    break;
                case RecoveryRecordCleanupDisposition.RecoveryRequired:
                    recoveryRequired++;
                    recordError ??= result.ErrorCode;
                    break;
                case RecoveryRecordCleanupDisposition.Skipped:
                    skipped++;
                    recordError ??= result.ErrorCode;
                    break;
            }
        }

        int processed = cleaned + alreadyClean + failed + recoveryRequired;
        var remainingResult = await _enumerator.CountCanonicalRecordsAsync(cancellationToken);
        int remaining = remainingResult.IsSuccess
            ? remainingResult.Value
            : snapshot.CanonicalRecords.Count - cleaned - alreadyClean;
        string? primary = snapshot.PrimaryErrorCode ?? recordError;
        if (remainingResult.IsFailure)
        {
            primary ??= remainingResult.Error!.Code;
        }

        bool blocking = failed > 0
            || recoveryRequired > 0
            || skipped > 0
            || snapshot.InvalidArtifactCount > 0
            || remaining > 0
            || remainingResult.IsFailure;
        return new RecoveryRunSummary(
            snapshot.CanonicalRecords.Count,
            processed,
            cleaned,
            alreadyClean,
            failed,
            recoveryRequired,
            skipped,
            snapshot.AuxiliaryArtifactCount,
            snapshot.InvalidArtifactCount,
            remaining,
            blocking,
            primary);
    }

    private static RecoveryRunSummary Empty(string errorCode) => new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, true, errorCode);
}
