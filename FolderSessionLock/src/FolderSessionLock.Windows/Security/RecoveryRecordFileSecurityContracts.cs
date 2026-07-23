using FolderSessionLock.Core.Results;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Security;

public enum RecoveryRecordFileKind
{
    CanonicalRecord,
    TemporaryRecord,
    BackupRecord
}

public sealed record RecoveryRecordFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdHigh,
    ulong FileIdLow,
    uint NumberOfLinks);

public sealed record RecoveryRecordFileSecuritySnapshot(
    RecoveryRecordFileKind FileKind,
    RecoveryRecordFileIdentity Identity,
    string OwnerSid,
    bool DaclPresent,
    bool DaclIsNull,
    bool DaclProtected,
    int ExplicitAceCount);

public interface IRecoveryRecordFileSecurity
{
    ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> ApplyAndVerifyAsync(
        SafeFileHandle fileHandle,
        RecoveryRecordFileKind fileKind,
        CancellationToken cancellationToken);

    ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> VerifyAsync(
        SafeFileHandle fileHandle,
        RecoveryRecordFileKind fileKind,
        CancellationToken cancellationToken);
}
