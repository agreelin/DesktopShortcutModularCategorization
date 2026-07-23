namespace FolderSessionLock.Broker.Recovery;

internal sealed record RecoveryServiceStatusSnapshot(
    RecoveryServiceState State,
    bool IsRunning,
    int Checkpoint,
    TimeSpan WaitHint,
    string? ErrorCode);
