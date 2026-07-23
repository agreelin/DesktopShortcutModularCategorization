namespace FolderSessionLock.Core.Models;

public sealed record LockTaskTransition(
    FolderLockTask Task,
    LockTaskTransitionOutcome Outcome);
