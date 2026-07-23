using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Models;

public sealed record LockTaskError
{
    public LockTaskError(Error detail, DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(detail);

        Detail = detail;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
    }

    public Error Detail { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
