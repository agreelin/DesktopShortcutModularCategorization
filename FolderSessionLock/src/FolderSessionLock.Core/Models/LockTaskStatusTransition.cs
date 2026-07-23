namespace FolderSessionLock.Core.Models;

public sealed record LockTaskStatusTransition(
    LockTaskStatus PreviousStatus,
    LockTaskStatus CurrentStatus,
    LockTaskTransitionOutcome Outcome);
