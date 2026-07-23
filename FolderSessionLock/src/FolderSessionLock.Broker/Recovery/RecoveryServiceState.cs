namespace FolderSessionLock.Broker.Recovery;

internal enum RecoveryServiceState
{
    StartPending,
    Preflight,
    Scanning,
    Ready,
    RecoveryBlocked,
    Stopping,
    Stopped,
}
