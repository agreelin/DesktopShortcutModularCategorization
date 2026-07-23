namespace FolderSessionLock.Core.Models;

public enum LockTaskStatus
{
    Created,
    Activating,
    Active,
    Unlocking,
    Completed,
    ActivationFailed,
    UnlockFailed,
    RecoveryRequired,
}
