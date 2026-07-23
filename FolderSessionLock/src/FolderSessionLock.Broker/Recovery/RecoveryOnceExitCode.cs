namespace FolderSessionLock.Broker.Recovery;

internal enum RecoveryOnceExitCode
{
    Success = 0,
    InvalidArguments = 2,
    ProtectedStorageSecurityFailure = 10,
    RecoveryEnumerationFailure = 11,
    RecoveryRecordLimitExceeded = 12,
    RecoveryBlocked = 13,
    Cancelled = 14,
    InternalFailure = 15,
}
