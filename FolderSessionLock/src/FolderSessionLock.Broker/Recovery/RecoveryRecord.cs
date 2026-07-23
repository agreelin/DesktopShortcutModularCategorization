using System.Security.AccessControl;

namespace FolderSessionLock.Broker.Recovery;

internal enum RecoveryRecordState
{
    Prepared,
    Applied,
    CleanupPending,
    CleanupFailed,
}

internal sealed record RecoveryRecord(
    int SchemaVersion,
    string WriterVersion,
    Guid RecordId,
    Guid TaskId,
    RecoveryRecordState State,
    string NormalizedPath,
    ulong VolumeSerialNumber,
    ulong FileIdHigh,
    ulong FileIdLow,
    string AccountSid,
    string LogonSid,
    uint WindowsSessionId,
    AccessControlType AceType,
    uint AccessMask,
    InheritanceFlags InheritanceFlags,
    PropagationFlags PropagationFlags,
    string AceFingerprintSha256,
    string BaselineDaclSha256,
    string? PostApplyDaclSha256,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset LastUpdatedUtc,
    int CleanupAttemptCount,
    string? LastErrorCode,
    string? LastErrorMessage);
