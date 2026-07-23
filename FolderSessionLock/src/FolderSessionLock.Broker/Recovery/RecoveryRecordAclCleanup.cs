using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;

namespace FolderSessionLock.Broker.Recovery;

internal class RecoveryRecordAclCleanup
{
    private readonly FileRecoveryRecordStore _store;
    private readonly WindowsFolderPathValidator _pathValidator;
    private readonly DirectoryAclEditor _aclEditor;
    private readonly IClock _clock;
    private readonly IRecoveryRecordAclCleanupTestHook? _testHook;

    internal RecoveryRecordAclCleanup(
        FileRecoveryRecordStore store,
        WindowsFolderPathValidator pathValidator,
        DirectoryAclEditor aclEditor,
        IClock clock,
        IRecoveryRecordAclCleanupTestHook? testHook = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pathValidator = pathValidator ?? throw new ArgumentNullException(nameof(pathValidator));
        _aclEditor = aclEditor ?? throw new ArgumentNullException(nameof(aclEditor));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _testHook = testHook;
    }

    internal virtual ValueTask<RecoveryRecordCleanupResult> ExecuteAsync(
        Guid recoveryRecordId,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(recoveryRecordId, null, cancellationToken);

    internal virtual ValueTask<RecoveryRecordCleanupResult> ExecuteAsync(
        RecoveryDirectoryRecord recoveryRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recoveryRecord);
        return recoveryRecord.ErrorCode is null
            ? ExecuteCoreAsync(recoveryRecord.RecordId, recoveryRecord, cancellationToken)
            : ValueTask.FromResult(RecoveryRecordCleanupResult.Failed(
                recoveryRecord.RecordId,
                recoveryRecord.ErrorCode));
    }

    private async ValueTask<RecoveryRecordCleanupResult> ExecuteCoreAsync(
        Guid recoveryRecordId,
        RecoveryDirectoryRecord? recoveryRecord,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return RecoveryRecordCleanupResult.Skipped(recoveryRecordId);
        }

        Result<RecoveryRecord> read = recoveryRecord is null
            ? await _store.ReadAsync(recoveryRecordId, cancellationToken)
            : await _store.ReadAsync(recoveryRecord, cancellationToken);
        if (read.IsFailure)
        {
            return FromFailure(recoveryRecordId, read.Error!);
        }

        RecoveryRecord record = read.Value;
        Result recordValidation = ValidateRecord(record, recoveryRecordId);
        if (recordValidation.IsFailure)
        {
            return FromFailure(recoveryRecordId, recordValidation.Error!);
        }

        Result<ValidatedDirectory> validation = _pathValidator.Validate(record.NormalizedPath);
        if (validation.IsFailure)
        {
            Error error = MapPathError(validation.Error!);
            return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                record,
                error.Code,
                cancellationToken));
        }

        using ValidatedDirectory directory = validation.Value;
        var expectedIdentity = new DirectoryIdentity(
            record.VolumeSerialNumber,
            record.FileIdHigh,
            record.FileIdLow);
        if (directory.Identity != expectedIdentity)
        {
            return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                record,
                BrokerErrorCodes.FSL_E_PATH_IDENTITY_CHANGED,
                cancellationToken));
        }

        Result beforeMapping = _pathValidator.VerifyCurrentPathMapping(directory);
        if (beforeMapping.IsFailure)
        {
            Error error = MapPathError(beforeMapping.Error!);
            return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                record,
                error.Code,
                cancellationToken));
        }

        _testHook?.BeforeAclCleanup(directory);

        Result<DirectoryAclSnapshot> snapshotResult = _aclEditor.ReadSnapshot(directory.Handle);
        if (snapshotResult.IsFailure)
        {
            return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                record,
                BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH,
                cancellationToken));
        }

        SecurityIdentifier logonSid = new(record.LogonSid);
        byte[] targetAce = DirectoryAclEditor.CreateTargetAce(logonSid);
        if (!string.Equals(
                RecoveryAclEvidence.ComputeAceFingerprint(targetAce),
                record.AceFingerprintSha256,
                StringComparison.Ordinal))
        {
            return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                record,
                BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH,
                cancellationToken));
        }

        DirectoryAclSnapshot current = snapshotResult.Value;
        int matchCount = current.AceBinaries.Count(
            ace => ace.AsSpan().SequenceEqual(targetAce));
        if (matchCount > 1)
        {
            return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                record,
                BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH,
                cancellationToken));
        }

        if (matchCount == 0)
        {
            if (!string.Equals(
                    RecoveryAclEvidence.ComputeDaclDigest(current),
                    record.BaselineDaclSha256,
                    StringComparison.Ordinal))
            {
                return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                    record,
                    BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH,
                    cancellationToken));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return RecoveryRecordCleanupResult.Skipped(recoveryRecordId);
            }

            Result<RecoveryRecord> pending = record.State == RecoveryRecordState.Prepared
                ? Result<RecoveryRecord>.Success(record)
                : await MarkCleanupPendingAsync(
                    record,
                    record.PostApplyDaclSha256!,
                    cancellationToken);
            if (pending.IsFailure)
            {
                return FromFailure(recoveryRecordId, pending.Error!);
            }

            _testHook?.AfterCleanupPending(directory);

            Result afterMapping = _pathValidator.VerifyCurrentPathMapping(directory);
            if (afterMapping.IsFailure)
            {
                Error error = MapPathError(afterMapping.Error!);
                return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                    record,
                    error.Code,
                    cancellationToken));
            }

            Result delete = await _store.DeleteCanonicalRecordAsync(
                pending.Value,
                cancellationToken);
            return delete.IsSuccess
                ? RecoveryRecordCleanupResult.AlreadyClean(recoveryRecordId)
                : FromFailure(recoveryRecordId, delete.Error!);
        }

        byte[] actualAce = current.AceBinaries.Single(
            ace => ace.AsSpan().SequenceEqual(targetAce));
        if (!string.Equals(
                RecoveryAclEvidence.ComputeAceFingerprint(actualAce),
                record.AceFingerprintSha256,
                StringComparison.Ordinal))
        {
            return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                record,
                BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH,
                cancellationToken));
        }

        DirectoryAclSnapshot baseline = DirectoryAclEditor.CreateBaselineSnapshot(
            current,
            targetAce);
        if (!string.Equals(
                RecoveryAclEvidence.ComputeDaclDigest(baseline),
                record.BaselineDaclSha256,
                StringComparison.Ordinal))
        {
            return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                record,
                BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH,
                cancellationToken));
        }

        string currentPostApply = RecoveryAclEvidence.ComputeDaclDigest(current);
        if (record.PostApplyDaclSha256 is not null
            && !string.Equals(
                currentPostApply,
                record.PostApplyDaclSha256,
                StringComparison.Ordinal))
        {
            return FromResult(recoveryRecordId, await FailValidatedDriftAsync(
                record,
                BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH,
                cancellationToken));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return RecoveryRecordCleanupResult.Skipped(recoveryRecordId);
        }

        Result<RecoveryRecord> pendingResult = await MarkCleanupPendingAsync(
            record,
            currentPostApply,
            cancellationToken);
        if (pendingResult.IsFailure)
        {
            return FromFailure(recoveryRecordId, pendingResult.Error!);
        }

        RecoveryRecord pendingRecord = pendingResult.Value;

        _testHook?.AfterCleanupPending(directory);

        var operation = new DirectoryAclOperation(
            directory.Handle,
            baseline,
            targetAce,
            new RecoveryAclEvidence(
                record.AceFingerprintSha256,
                record.BaselineDaclSha256,
                currentPostApply));
        Result remove = _aclEditor.RemoveDenyAce(directory.Handle, operation);
        if (remove.IsFailure)
        {
            return FromResult(recoveryRecordId, await FailCleanupAsync(
                pendingRecord,
                remove.Error!.Category == ErrorCategory.PlatformError
                    ? BrokerErrorCodes.FSL_E_ACL_REMOVE_FAILED
                    : BrokerErrorCodes.FSL_E_ACL_POST_VERIFY_FAILED,
                cancellationToken));
        }

        Result afterRemovalMapping = _pathValidator.VerifyCurrentPathMapping(directory);
        if (afterRemovalMapping.IsFailure)
        {
            return FromResult(recoveryRecordId, await FailCleanupAsync(
                pendingRecord,
                BrokerErrorCodes.FSL_E_PATH_IDENTITY_CHANGED,
                cancellationToken));
        }

        Result finalDelete = await _store.DeleteCanonicalRecordAsync(
            pendingRecord,
            cancellationToken);
        return finalDelete.IsSuccess
            ? RecoveryRecordCleanupResult.Cleaned(recoveryRecordId)
            : FromFailure(recoveryRecordId, finalDelete.Error!);
    }

    private async ValueTask<Result<RecoveryRecord>> MarkCleanupPendingAsync(
        RecoveryRecord record,
        string postApplyDaclSha256,
        CancellationToken cancellationToken)
    {
        if (record.CleanupAttemptCount >= 1_000_000)
        {
            return Result<RecoveryRecord>.Failure(
                Error(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED));
        }

        RecoveryRecord pending = record with
        {
            State = RecoveryRecordState.CleanupPending,
            PostApplyDaclSha256 = postApplyDaclSha256,
            CleanupAttemptCount = record.CleanupAttemptCount + 1,
            LastErrorCode = null,
            LastErrorMessage = null,
            LastUpdatedUtc = _clock.UtcNow.ToUniversalTime(),
        };
        Result update = await _store.UpdateAsync(pending, cancellationToken);
        return update.IsSuccess
            ? Result<RecoveryRecord>.Success(pending)
            : Result<RecoveryRecord>.Failure(update.Error!);
    }

    private async ValueTask<Result> FailCleanupAsync(
        RecoveryRecord pending,
        string code,
        CancellationToken cancellationToken)
    {
        Result update = await _store.UpdateAsync(pending with
        {
            State = RecoveryRecordState.CleanupFailed,
            LastErrorCode = code,
            LastErrorMessage = code,
            LastUpdatedUtc = _clock.UtcNow.ToUniversalTime(),
        }, cancellationToken);
        return update.IsFailure ? update : Failure(code);
    }

    private ValueTask<Result> FailValidatedDriftAsync(
        RecoveryRecord record,
        string code,
        CancellationToken cancellationToken)
    {
        if (record.PostApplyDaclSha256 is null || record.CleanupAttemptCount >= 1_000_000)
        {
            return ValueTask.FromResult(Failure(code));
        }

        return FailCleanupAsync(record with
        {
            State = RecoveryRecordState.CleanupPending,
            CleanupAttemptCount = record.CleanupAttemptCount + 1,
            LastErrorCode = null,
            LastErrorMessage = null,
        }, code, cancellationToken);
    }

    private static Result ValidateRecord(RecoveryRecord record, Guid requestedRecordId)
    {
        if (record.RecordId != requestedRecordId
            || record.AceType != AccessControlType.Deny
            || record.InheritanceFlags
                != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
            || record.PropagationFlags != PropagationFlags.None)
        {
            return Failure(record.RecordId != requestedRecordId
                ? BrokerErrorCodes.FSL_E_RECOVERY_RECORD_ID_MISMATCH
                : BrokerErrorCodes.FSL_E_RECOVERY_RECORD_MISMATCH);
        }

        return record.AccessMask == (uint)FolderDenyAccessMask.Value
            ? Result.Success()
            : Failure(BrokerErrorCodes.FSL_E_RECOVERY_ACCESS_MASK_UNSUPPORTED);
    }

    private static Error MapPathError(Error error) => error.Code switch
    {
        "windows.path.empty" => Error(BrokerErrorCodes.FSL_E_PATH_EMPTY),
        "windows.path.relative" => Error(BrokerErrorCodes.FSL_E_PATH_NOT_ABSOLUTE),
        "windows.path.invalid" => Error(BrokerErrorCodes.FSL_E_PATH_INVALID),
        "windows.path.not_found" => Error(BrokerErrorCodes.FSL_E_PATH_NOT_FOUND),
        "windows.path.not_directory" => Error(BrokerErrorCodes.FSL_E_PATH_NOT_DIRECTORY),
        "windows.path.root" => Error(BrokerErrorCodes.FSL_E_PATH_ROOT_FORBIDDEN),
        "windows.path.protected" => Error(BrokerErrorCodes.FSL_E_PATH_NOT_ALLOWED),
        "windows.path.unc" => Error(BrokerErrorCodes.FSL_E_PATH_NETWORK_FORBIDDEN),
        "windows.path.drive_not_fixed" => Error(BrokerErrorCodes.FSL_E_PATH_DRIVE_TYPE_UNSUPPORTED),
        "windows.path.file_system_not_ntfs" => Error(BrokerErrorCodes.FSL_E_PATH_FILESYSTEM_UNSUPPORTED),
        "windows.path.reparse_point" => Error(BrokerErrorCodes.FSL_E_PATH_REPARSE_POINT_FORBIDDEN),
        "windows.path.insufficient_permissions" => Error(BrokerErrorCodes.FSL_E_PATH_ACCESS_DENIED),
        "windows.path.final_path_mismatch" or "windows.path.mapping_changed" =>
            Error(BrokerErrorCodes.FSL_E_PATH_IDENTITY_CHANGED),
        _ => Error(BrokerErrorCodes.FSL_E_PATH_IDENTITY_UNAVAILABLE),
    };

    private static Result Failure(string code) => Result.Failure(Error(code));

    private static RecoveryRecordCleanupResult FromResult(Guid recordId, Result result) =>
        result.IsFailure
            ? FromFailure(recordId, result.Error!)
            : RecoveryRecordCleanupResult.AlreadyClean(recordId);

    private static RecoveryRecordCleanupResult FromFailure(Guid recordId, Error error) =>
        IsRecoveryRequired(error.Code)
            ? RecoveryRecordCleanupResult.RecoveryRequired(recordId, error.Code)
            : RecoveryRecordCleanupResult.Failed(recordId, error.Code);

    private static bool IsRecoveryRequired(string code) => code is
        BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED
        or BrokerErrorCodes.FSL_E_RECOVERY_RECORD_WRITE_FAILED
        or BrokerErrorCodes.FSL_E_RECOVERY_RECORD_DELETE_FAILED
        or BrokerErrorCodes.FSL_E_ACL_REMOVE_FAILED
        or BrokerErrorCodes.FSL_E_ACL_POST_VERIFY_FAILED;

    private static Error Error(string code) => new(code, code, ErrorCategory.UnrecoverableError);
}

internal interface IRecoveryRecordAclCleanupTestHook
{
    void BeforeAclCleanup(ValidatedDirectory directory);

    void AfterCleanupPending(ValidatedDirectory directory)
    {
    }
}
