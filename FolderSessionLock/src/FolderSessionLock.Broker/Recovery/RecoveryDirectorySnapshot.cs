using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Broker.Recovery;

internal sealed record RecoveryDirectoryRecord(
    Guid RecordId,
    string FileName,
    string FullPath,
    RecoveryRecordFileIdentity? FileIdentity,
    string? ErrorCode);

internal sealed record RecoveryDirectorySnapshot(
    IReadOnlyList<RecoveryDirectoryRecord> CanonicalRecords,
    int AuxiliaryArtifactCount,
    int InvalidArtifactCount,
    string? PrimaryErrorCode,
    RecoveryRecordFileIdentity DirectoryIdentity);
