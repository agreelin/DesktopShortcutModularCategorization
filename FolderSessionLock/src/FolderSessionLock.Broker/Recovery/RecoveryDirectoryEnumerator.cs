using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery;

internal sealed class RecoveryDirectoryEnumerator
{
    internal const int MaximumTotalEntryCount = 4096;
    internal const int MaximumCanonicalRecordCount = 1024;

    private readonly string _recordsDirectory;
    private readonly IRecoveryRecordFileSecurity _fileSecurity;
    private readonly IRecoveryStoreFilePlatform _filePlatform;

    internal RecoveryDirectoryEnumerator(
        string recordsDirectory,
        IRecoveryRecordFileSecurity fileSecurity,
        IRecoveryStoreFilePlatform filePlatform)
    {
        _recordsDirectory = Path.GetFullPath(recordsDirectory);
        _fileSecurity = fileSecurity ?? throw new ArgumentNullException(nameof(fileSecurity));
        _filePlatform = filePlatform ?? throw new ArgumentNullException(nameof(filePlatform));
    }

    internal async ValueTask<Result<RecoveryDirectorySnapshot>> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_recordsDirectory))
        {
            return Failure<RecoveryDirectorySnapshot>(
                BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_OPEN_FAILED);
        }

        Result<SafeFileHandle> directoryOpen = _filePlatform.OpenDirectory(_recordsDirectory);
        if (directoryOpen.IsFailure)
        {
            return Failure<RecoveryDirectorySnapshot>(
                BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_OPEN_FAILED);
        }

        using SafeFileHandle directoryHandle = directoryOpen.Value;
        Result<RecoveryRecordFileIdentity> directoryIdentity = _filePlatform.GetIdentity(
            directoryHandle);
        if (directoryIdentity.IsFailure)
        {
            return Failure<RecoveryDirectorySnapshot>(
                BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_UNAVAILABLE);
        }

        string[] entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(
                    _recordsDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Take(MaximumTotalEntryCount + 1)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Failure<RecoveryDirectorySnapshot>(
                BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_OPEN_FAILED);
        }
        catch (IOException)
        {
            return Failure<RecoveryDirectorySnapshot>(
                BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_ENUMERATION_FAILED);
        }

        if (entries.Length > MaximumTotalEntryCount)
        {
            return Failure<RecoveryDirectorySnapshot>(
                BrokerErrorCodes.FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED);
        }

        var metadata = new List<Entry>(entries.Length);
        foreach (string entryPath in entries)
        {
            try
            {
                metadata.Add(new Entry(
                    Path.GetFileName(entryPath),
                    entryPath,
                    File.GetAttributes(entryPath)));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return Failure<RecoveryDirectorySnapshot>(
                    BrokerErrorCodes.FSL_E_RECOVERY_ENTRY_METADATA_FAILED);
            }
        }

        metadata.Sort((left, right) => StringComparer.Ordinal.Compare(left.FileName, right.FileName));
        var canonicalEntries = metadata
            .Where(entry => TryParseCanonicalRecord(entry.FileName, out _))
            .ToArray();
        if (canonicalEntries.Length > MaximumCanonicalRecordCount)
        {
            return Failure<RecoveryDirectorySnapshot>(
                BrokerErrorCodes.FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED);
        }

        var canonical = new List<RecoveryDirectoryRecord>(canonicalEntries.Length);
        foreach (Entry entry in canonicalEntries)
        {
            _ = TryParseCanonicalRecord(entry.FileName, out Guid recordId);
            canonical.Add(await InspectCanonicalAsync(
                directoryHandle,
                directoryIdentity.Value,
                entry,
                recordId,
                cancellationToken));
        }

        HashSet<Guid> canonicalIds = canonical.Select(record => record.RecordId).ToHashSet();
        int auxiliaryCount = 0;
        int invalidCount = 0;
        string? primaryErrorCode = null;
        foreach (Entry entry in metadata)
        {
            if (TryParseCanonicalRecord(entry.FileName, out _))
            {
                continue;
            }

            ArtifactClassification classification = await ClassifyArtifactAsync(
                directoryHandle,
                directoryIdentity.Value,
                entry,
                canonicalIds,
                cancellationToken);
            if (classification.IsAuxiliary)
            {
                auxiliaryCount++;
            }
            else
            {
                invalidCount++;
                primaryErrorCode ??= classification.ErrorCode;
            }
        }

        Result identityCheck = VerifyIdentity(directoryHandle, directoryIdentity.Value);
        return identityCheck.IsSuccess
            ? Result<RecoveryDirectorySnapshot>.Success(new(
                canonical,
                auxiliaryCount,
                invalidCount,
                primaryErrorCode,
                directoryIdentity.Value))
            : Result<RecoveryDirectorySnapshot>.Failure(identityCheck.Error!);
    }

    internal Result VerifyIdentity(RecoveryRecordFileIdentity expected)
    {
        Result<SafeFileHandle> open = _filePlatform.OpenDirectory(_recordsDirectory);
        if (open.IsFailure)
        {
            return Failure(BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_CHANGED);
        }

        using SafeFileHandle handle = open.Value;
        return VerifyIdentity(handle, expected);
    }

    internal async ValueTask<Result<int>> CountCanonicalRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        Result<RecoveryDirectorySnapshot> snapshot = await EnumerateAsync(cancellationToken);
        return snapshot.IsSuccess
            ? Result<int>.Success(snapshot.Value.CanonicalRecords.Count)
            : Result<int>.Failure(snapshot.Error!);
    }

    private async ValueTask<RecoveryDirectoryRecord> InspectCanonicalAsync(
        SafeFileHandle directoryHandle,
        RecoveryRecordFileIdentity directoryIdentity,
        Entry entry,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        Result<RecoveryRecordFileIdentity> security = await InspectFileAsync(
            directoryHandle,
            directoryIdentity,
            entry,
            RecoveryRecordFileKind.CanonicalRecord,
            cancellationToken);
        return new RecoveryDirectoryRecord(
            recordId,
            entry.FileName,
            entry.FullPath,
            security.IsSuccess ? security.Value : null,
            security.IsSuccess ? null : security.Error!.Code);
    }

    private async ValueTask<ArtifactClassification> ClassifyArtifactAsync(
        SafeFileHandle directoryHandle,
        RecoveryRecordFileIdentity directoryIdentity,
        Entry entry,
        IReadOnlySet<Guid> canonicalIds,
        CancellationToken cancellationToken)
    {
        if (TryParseAuxiliary(entry.FileName, ".bak", out Guid backupId))
        {
            Result<RecoveryRecordFileIdentity> security = await InspectFileAsync(
                directoryHandle,
                directoryIdentity,
                entry,
                RecoveryRecordFileKind.BackupRecord,
                cancellationToken);
            if (security.IsFailure)
            {
                return new(false, BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID);
            }

            return canonicalIds.Contains(backupId)
                ? new(true, null)
                : new(false, BrokerErrorCodes.FSL_E_RECOVERY_BACKUP_ORPHANED);
        }

        int marker = entry.FileName.IndexOf(".tmp-", StringComparison.Ordinal);
        if (marker > 0
            && TryParseCanonicalGuid(entry.FileName[..marker], out Guid recordId)
            && TryParseCanonicalGuid(entry.FileName[(marker + 5)..], out _))
        {
            Result<RecoveryRecordFileIdentity> security = await InspectFileAsync(
                directoryHandle,
                directoryIdentity,
                entry,
                RecoveryRecordFileKind.TemporaryRecord,
                cancellationToken);
            if (security.IsFailure)
            {
                return new(false, BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID);
            }

            return canonicalIds.Contains(recordId)
                ? new(true, null)
                : new(false, BrokerErrorCodes.FSL_E_RECOVERY_TEMP_ORPHANED);
        }

        return new(false, BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_INVALID);
    }

    private async ValueTask<Result<RecoveryRecordFileIdentity>> InspectFileAsync(
        SafeFileHandle directoryHandle,
        RecoveryRecordFileIdentity directoryIdentity,
        Entry entry,
        RecoveryRecordFileKind fileKind,
        CancellationToken cancellationToken)
    {
        if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            return Failure<RecoveryRecordFileIdentity>(
                BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID);
        }

        Result<SafeFileHandle> open = _filePlatform.OpenExisting(
            directoryHandle,
            entry.FileName);
        if (open.IsFailure)
        {
            return Failure<RecoveryRecordFileIdentity>(
                BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID);
        }

        using SafeFileHandle handle = open.Value;
        Result<NativeMethods.FileAttributeTagInfo> attributes = _filePlatform.GetAttributes(handle);
        Result<string> finalPath = _filePlatform.GetFinalPath(handle);
        Result<RecoveryRecordFileSecuritySnapshot> security = await _fileSecurity.VerifyAsync(
            handle,
            fileKind,
            cancellationToken);
        if (attributes.IsFailure
            || (attributes.Value.FileAttributes
                & (NativeMethods.FileAttributeDirectory | NativeMethods.FileAttributeReparsePoint)) != 0
            || finalPath.IsFailure
            || !string.Equals(entry.FullPath, finalPath.Value, StringComparison.OrdinalIgnoreCase))
        {
            return Failure<RecoveryRecordFileIdentity>(
                fileKind == RecoveryRecordFileKind.CanonicalRecord
                    ? BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH
                    : BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID);
        }

        if (security.IsFailure)
        {
            return fileKind == RecoveryRecordFileKind.CanonicalRecord
                ? Result<RecoveryRecordFileIdentity>.Failure(security.Error!)
                : Failure<RecoveryRecordFileIdentity>(
                    BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID);
        }

        if (security.Value.Identity.NumberOfLinks != 1
            || security.Value.Identity.VolumeSerialNumber != directoryIdentity.VolumeSerialNumber)
        {
            return Failure<RecoveryRecordFileIdentity>(
                fileKind == RecoveryRecordFileKind.CanonicalRecord
                    ? BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH
                    : BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID);
        }

        return Result<RecoveryRecordFileIdentity>.Success(security.Value.Identity);
    }

    private Result VerifyIdentity(
        SafeFileHandle handle,
        RecoveryRecordFileIdentity expected)
    {
        Result<RecoveryRecordFileIdentity> actual = _filePlatform.GetIdentity(handle);
        return actual.IsSuccess && actual.Value == expected
            ? Result.Success()
            : Failure(BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_CHANGED);
    }

    private static bool TryParseCanonicalRecord(string fileName, out Guid recordId)
    {
        recordId = Guid.Empty;
        return fileName.EndsWith(".fslr", StringComparison.Ordinal)
            && TryParseCanonicalGuid(fileName[..^5], out recordId);
    }

    private static bool TryParseAuxiliary(string fileName, string extension, out Guid recordId)
    {
        recordId = Guid.Empty;
        return fileName.EndsWith(extension, StringComparison.Ordinal)
            && TryParseCanonicalGuid(fileName[..^extension.Length], out recordId);
    }

    private static bool TryParseCanonicalGuid(string value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id)
        && id != Guid.Empty
        && value == id.ToString("D");

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(new Error(
        code,
        code,
        ErrorCategory.UnrecoverableError));

    private static Result Failure(string code) => Result.Failure(new Error(
        code,
        code,
        ErrorCategory.UnrecoverableError));

    private sealed record Entry(string FileName, string FullPath, FileAttributes Attributes);

    private sealed record ArtifactClassification(bool IsAuxiliary, string? ErrorCode);
}
