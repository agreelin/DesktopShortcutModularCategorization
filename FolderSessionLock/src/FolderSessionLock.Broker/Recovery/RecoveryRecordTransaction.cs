using System.Security.AccessControl;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;

namespace FolderSessionLock.Broker.Recovery;

internal sealed class RecoveryRecordTransaction : IFolderLockRecoveryTransaction
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileRecoveryRecordStore _store;
    private readonly RecoveryTaskRegistry _registry;
    private readonly IClock _clock;

    internal RecoveryRecordTransaction(
        FileRecoveryRecordStore store,
        RecoveryTaskRegistry registry,
        IClock clock)
    {
        _store = store;
        _registry = registry;
        _clock = clock;
    }

    public async ValueTask<Result<Guid>> PrepareAsync(
        FolderLockRequest request,
        SessionIdentity sessionIdentity,
        ValidatedDirectory directory,
        RecoveryAclEvidence evidence,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset createdUtc = _clock.UtcNow.ToUniversalTime();
            DateTimeOffset expiresUtc;
            try
            {
                expiresUtc = createdUtc.Add(request.Duration);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Result<Guid>.Failure(Failure(BrokerErrorCodes.FSL_E_DURATION_OUT_OF_RANGE));
            }

            var record = new RecoveryRecord(
                1,
                "1.0",
                Guid.NewGuid(),
                request.TaskId,
                RecoveryRecordState.Prepared,
                directory.NormalizedPath,
                directory.Identity.VolumeSerialNumber,
                directory.Identity.FileIdHigh,
                directory.Identity.FileIdLow,
                sessionIdentity.AccountSid,
                sessionIdentity.LogonSid,
                checked((uint)sessionIdentity.WindowsSessionId),
                AccessControlType.Deny,
                (uint)FolderDenyAccessMask.Value,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                evidence.AceFingerprintSha256,
                evidence.BaselineDaclSha256,
                null,
                createdUtc,
                expiresUtc,
                createdUtc,
                0,
                null,
                null);

            if (!_registry.TryAdd(record))
            {
                return Result<Guid>.Failure(Failure(BrokerErrorCodes.FSL_E_TASK_ID_CONFLICT));
            }

            Result write = await _store.WriteNewAsync(record, cancellationToken);
            if (write.IsFailure)
            {
                _registry.Remove(record.RecordId);
                return Result<Guid>.Failure(write.Error!);
            }

            return Result<Guid>.Success(record.RecordId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<Result> MarkAppliedAsync(
        Guid recoveryRecordId,
        RecoveryAclEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (evidence.PostApplyDaclSha256 is null)
        {
            return ValueTask.FromResult(Result.Failure(Failure(
                BrokerErrorCodes.FSL_E_ACL_POST_VERIFY_FAILED)));
        }

        return UpdateAsync(recoveryRecordId, record => record with
        {
            State = RecoveryRecordState.Applied,
            AceFingerprintSha256 = evidence.AceFingerprintSha256,
            PostApplyDaclSha256 = evidence.PostApplyDaclSha256,
            LastUpdatedUtc = _clock.UtcNow.ToUniversalTime(),
        }, cancellationToken);
    }

    public ValueTask<Result> MarkCleanupPendingAsync(
        Guid recoveryRecordId,
        CancellationToken cancellationToken) =>
        UpdateAsync(recoveryRecordId, record =>
        {
            if (record.CleanupAttemptCount >= 1_000_000)
            {
                return null;
            }

            return record with
            {
                State = RecoveryRecordState.CleanupPending,
                CleanupAttemptCount = record.CleanupAttemptCount + 1,
                LastErrorCode = null,
                LastErrorMessage = null,
                LastUpdatedUtc = _clock.UtcNow.ToUniversalTime(),
            };
        }, cancellationToken);

    public ValueTask<Result> MarkCleanupFailedAsync(
        Guid recoveryRecordId,
        Error error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        return UpdateAsync(recoveryRecordId, record => record with
        {
            State = RecoveryRecordState.CleanupFailed,
            LastErrorCode = IsProtocolErrorCode(error.Code)
                ? error.Code
                : BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
            LastErrorMessage = IsProtocolErrorCode(error.Code)
                ? error.Code
                : BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
            LastUpdatedUtc = _clock.UtcNow.ToUniversalTime(),
        }, cancellationToken);
    }

    public async ValueTask<Result> DeleteAsync(
        Guid recoveryRecordId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RecoveryRecord? current = _registry.GetByRecordId(recoveryRecordId);
            if (current is null)
            {
                return Result.Failure(Failure(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_NOT_FOUND));
            }

            Result delete = await _store.DeleteAsync(current, cancellationToken);
            if (delete.IsSuccess)
            {
                _registry.Remove(recoveryRecordId);
            }

            return delete;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<Result> UpdateAsync(
        Guid recordId,
        Func<RecoveryRecord, RecoveryRecord?> update,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RecoveryRecord? current = _registry.GetByRecordId(recordId);
            if (current is null)
            {
                return Result.Failure(Failure(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_NOT_FOUND));
            }

            RecoveryRecord? updated = update(current);
            if (updated is null || updated.LastUpdatedUtc < current.LastUpdatedUtc)
            {
                return Result.Failure(Failure(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED));
            }

            Result write = await _store.UpdateAsync(updated, cancellationToken);
            if (write.IsFailure)
            {
                return write;
            }

            return _registry.Update(updated)
                ? Result.Success()
                : Result.Failure(Failure(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_MISMATCH));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsProtocolErrorCode(string code) =>
        code.StartsWith("FSL_E_", StringComparison.Ordinal)
        && code.Length <= 128
        && code[6..].Length > 0
        && code[6] != '_'
        && code[^1] != '_'
        && !code.Contains("__", StringComparison.Ordinal)
        && code[6..].All(character => character == '_' || character is >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static Error Failure(string code) => new(code, code, ErrorCategory.UnrecoverableError);
}
