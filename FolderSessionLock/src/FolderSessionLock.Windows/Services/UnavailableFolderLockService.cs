using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Windows.Services;

public sealed class UnavailableFolderLockService : IFolderLockService
{
    private static readonly Error NotImplementedError = new(
        "windows.acl.not_implemented",
        "Windows ACL operations are not implemented in stage 1.",
        ErrorCategory.PlatformError);

    public ValueTask<Result<Guid>> CreateLockAsync(
        FolderLockRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(Result<Guid>.Failure(NotImplementedError));
    }

    public ValueTask<Result> RemoveLockAsync(
        Guid taskId,
        LockRemovalIntent intent,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result.Failure(NotImplementedError));
}
