namespace FolderSessionLock.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}
