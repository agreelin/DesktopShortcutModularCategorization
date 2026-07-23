using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Models;

public readonly record struct FolderLockTaskId
{
    private FolderLockTaskId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public bool IsValid => Value != Guid.Empty;

    public static FolderLockTaskId New() => new(Guid.NewGuid());

    public static Result<FolderLockTaskId> Create(Guid value) => value == Guid.Empty
        ? Result<FolderLockTaskId>.Failure(new Error(
            "lock_task.id.empty",
            "A folder lock task ID cannot be empty.",
            ErrorCategory.ValidationFailed))
        : Result<FolderLockTaskId>.Success(new FolderLockTaskId(value));

    public override string ToString() => Value.ToString("D");
}
