using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Abstractions;

public interface IFolderLockService
{
    ValueTask<Result<Guid>> CreateLockAsync(
        FolderLockRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<Result> RemoveLockAsync(
        Guid taskId,
        LockRemovalIntent intent,
        CancellationToken cancellationToken = default);
}
