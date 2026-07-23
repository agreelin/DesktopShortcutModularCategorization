using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Abstractions;

public interface IAccessAttemptMonitor
{
    ValueTask<Result> StartAsync(
        Guid taskId,
        string normalizedPath,
        CancellationToken cancellationToken = default);

    ValueTask<Result> StopAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}
