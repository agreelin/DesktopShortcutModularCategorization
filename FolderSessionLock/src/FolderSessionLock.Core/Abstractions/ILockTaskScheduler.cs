using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Abstractions;

public interface ILockTaskScheduler
{
    ValueTask<Result<int>> ProcessDueTasksAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Result> RunAsync(CancellationToken cancellationToken = default);
}
