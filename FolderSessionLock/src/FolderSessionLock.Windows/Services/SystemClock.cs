using FolderSessionLock.Core.Abstractions;

namespace FolderSessionLock.Windows.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();

    public long GetTimestamp() => TimeProvider.System.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeProvider.System.GetElapsedTime(startingTimestamp, endingTimestamp);

    public ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default) =>
        new(Task.Delay(delay, TimeProvider.System, cancellationToken));
}
