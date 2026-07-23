using FolderSessionLock.Core.Abstractions;

namespace FolderSessionLock.Core.Tests.Infrastructure;

internal sealed class FixedClock(DateTimeOffset utcNow, long timestamp) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;

    public long GetTimestamp() => timestamp;

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

    public ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled(cancellationToken)
            : ValueTask.CompletedTask;
}
