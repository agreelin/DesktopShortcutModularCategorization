using FolderSessionLock.Core.Abstractions;

namespace FolderSessionLock.Core.Tests.Infrastructure;

internal sealed class ControllableClock : IClock
{
    private readonly object _gate = new();
    private readonly List<DelayWaiter> _delayWaiters = [];
    private readonly List<TimeSpan> _scheduledDelays = [];
    private readonly TaskCompletionSource _delayScheduled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public ControllableClock(DateTimeOffset utcNow)
    {
        _utcNow = utcNow.ToUniversalTime();
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }
    }

    public long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public Task DelayScheduled => _delayScheduled.Task;

    public IReadOnlyList<TimeSpan> ScheduledDelays
    {
        get
        {
            lock (_gate)
            {
                return _scheduledDelays.ToArray();
            }
        }
    }

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

    public ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        lock (_gate)
        {
            if (delay == TimeSpan.Zero)
            {
                return ValueTask.CompletedTask;
            }

            var waiter = new DelayWaiter(_timestamp + delay.Ticks, cancellationToken);
            _delayWaiters.Add(waiter);
            _scheduledDelays.Add(delay);
            _delayScheduled.TrySetResult();
            return new ValueTask(waiter.Task);
        }
    }

    public void Advance(TimeSpan elapsed)
    {
        AdvanceMonotonic(elapsed);
        AdvanceWallClock(elapsed);
    }

    public void AdvanceMonotonic(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        DelayWaiter[] dueWaiters;
        lock (_gate)
        {
            _timestamp += elapsed.Ticks;
            dueWaiters = _delayWaiters.Where(waiter => waiter.DueTimestamp <= _timestamp).ToArray();
            _delayWaiters.RemoveAll(waiter => waiter.DueTimestamp <= _timestamp);
        }

        foreach (DelayWaiter waiter in dueWaiters)
        {
            waiter.Complete();
        }
    }

    public void AdvanceWallClock(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _utcNow = _utcNow.Add(elapsed);
        }
    }

    public void SetWallClock(DateTimeOffset value)
    {
        lock (_gate)
        {
            _utcNow = value.ToUniversalTime();
        }
    }

    private sealed class DelayWaiter
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public DelayWaiter(long dueTimestamp, CancellationToken cancellationToken)
        {
            DueTimestamp = dueTimestamp;
            _registration = cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
        }

        public long DueTimestamp { get; }

        public Task Task => _completion.Task;

        public void Complete()
        {
            _registration.Dispose();
            _completion.TrySetResult();
        }
    }
}
