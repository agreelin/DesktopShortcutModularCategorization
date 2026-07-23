using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Windows.Services;

public sealed class DisabledAccessAttemptMonitor : IAccessAttemptMonitor
{
    private static readonly Error DisabledError = new(
        "windows.access_monitor.disabled",
        "Access attempt monitoring is disabled.",
        ErrorCategory.RecoverableError);

    public ValueTask<Result> StartAsync(
        Guid taskId,
        string normalizedPath,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result.Failure(DisabledError));

    public ValueTask<Result> StopAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result.Success());
}
