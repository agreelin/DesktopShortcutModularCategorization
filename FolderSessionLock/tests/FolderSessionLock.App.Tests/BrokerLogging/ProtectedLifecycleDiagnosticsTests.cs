using System.Text;
using System.Text.Json;
using FolderSessionLock.Broker.Lifecycle;
using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.App.Tests.BrokerLogging;

public sealed class ProtectedLifecycleDiagnosticsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 1, 0, 0, TimeSpan.Zero);
    private static readonly Guid FirstTaskId =
        Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid LaterTaskId =
        Guid.Parse("00000000-0000-4000-8000-000000000002");

    [Fact]
    public async Task SchedulerUnexpectedFailure_DoesNotOverrideCleanupFirstTaskError()
    {
        var platform = new RecordingPlatform();
        using var provider = new ProtectedJsonLinesLoggerProvider(
            ProtectedLoggerMode.ConsentBroker,
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            platform,
            () => Now,
            processId: 1234);
        Assert.True(provider.Initialize().IsSuccess);
        using var loggerFactory = new ProtectedLoggerFactory(provider);
        var clock = new FixedClock();
        var manager = new LockTaskManager(new UnrelatedPathRelationService());
        var lockService = new FailingFolderLockService();
        var coordinator = new LockTaskCoordinator(
            manager,
            lockService,
            clock,
            loggerFactory.CreateLogger<LockTaskCoordinator>());
        var lifecycle = new BrokerLifecycleController(
            new FailingScheduler(),
            coordinator,
            loggerFactory.CreateLogger<BrokerLifecycleController>());
        await AddAndActivate(manager, coordinator, FirstTaskId, @"C:\Tasks\First");
        await AddAndActivate(manager, coordinator, LaterTaskId, @"C:\Tasks\Later");

        Result scheduler = await lifecycle.RunSchedulerAsync();
        Result<int> cleanup = await lifecycle.StopAsync();

        Assert.True(scheduler.IsFailure);
        Assert.True(cleanup.IsFailure);
        Assert.Equal("lock_task.cleanup.first", cleanup.Error!.Code);
        JsonElement[] lines = platform.ReadLines();
        Assert.Equal(
            [
                "SchedulerStopped",
                "LifecycleCleanupTaskFailed",
                "LifecycleCleanupTaskFailed",
                "LifecycleCleanupRecoveryRequired",
            ],
            lines.Select(line => line.GetProperty("eventName").GetString()));
        Assert.Equal(
            [
                "lock_task.scheduler.loop.exception",
                "lock_task.cleanup.first",
                "lock_task.cleanup.later",
                "lock_task.cleanup.first",
            ],
            lines.Select(line => line.GetProperty("errorCode").GetString()));
        Assert.Equal(
            FirstTaskId.ToString("D"),
            lines[1].GetProperty("taskId").GetString());
        Assert.Equal(
            LaterTaskId.ToString("D"),
            lines[2].GetProperty("taskId").GetString());
        Assert.Equal("Error", lines[0].GetProperty("level").GetString());
        Assert.Equal("Scheduler", lines[0].GetProperty("component").GetString());
        Assert.Equal(
            "The lock task scheduler loop terminated unexpectedly.",
            lines[0].GetProperty("message").GetString());
        string json = Encoding.UTF8.GetString(platform.Bytes.ToArray());
        Assert.DoesNotContain("Sensitive first cleanup failure", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive later cleanup failure", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ExceptionType", json, StringComparison.Ordinal);
        Assert.Equal(lines.Length, platform.FlushCount);
    }

    [Fact]
    public async Task SchedulerUnexpectedFailure_WritesExactProtectedEventAndKeepsProviderHealthy()
    {
        var platform = new RecordingPlatform();
        using var provider = new ProtectedJsonLinesLoggerProvider(
            ProtectedLoggerMode.ConsentBroker,
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            platform,
            () => Now,
            processId: 1234);
        Assert.True(provider.Initialize().IsSuccess);
        using var loggerFactory = new ProtectedLoggerFactory(provider);
        var coordinator = new LockTaskCoordinator(
            new LockTaskManager(new UnrelatedPathRelationService()),
            new FailingFolderLockService(),
            new FixedClock(),
            loggerFactory.CreateLogger<LockTaskCoordinator>());
        var lifecycle = new BrokerLifecycleController(
            new FailingScheduler(),
            coordinator,
            loggerFactory.CreateLogger<BrokerLifecycleController>());

        Result scheduler = await lifecycle.RunSchedulerAsync();
        Result<int> cleanup = await lifecycle.StopAsync();

        Assert.True(scheduler.IsFailure);
        Assert.True(cleanup.IsSuccess);
        Assert.False(provider.IsPermanentlyFailed);
        JsonElement[] lines = platform.ReadLines();
        Assert.Equal(2, lines.Length);
        Assert.Equal("SchedulerStopped", lines[0].GetProperty("eventName").GetString());
        Assert.Equal("Error", lines[0].GetProperty("level").GetString());
        Assert.Equal("Scheduler", lines[0].GetProperty("component").GetString());
        Assert.Equal(
            "lock_task.scheduler.loop.exception",
            lines[0].GetProperty("errorCode").GetString());
        Assert.Equal(
            "The lock task scheduler loop terminated unexpectedly.",
            lines[0].GetProperty("message").GetString());
        Assert.Equal(
            "LifecycleCleanupCompleted",
            lines[1].GetProperty("eventName").GetString());
        string json = Encoding.UTF8.GetString(platform.Bytes.ToArray());
        Assert.DoesNotContain("Sensitive scheduler failure.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
        Assert.Equal(lines.Length, platform.FlushCount);
    }

    private static async Task AddAndActivate(
        LockTaskManager manager,
        LockTaskCoordinator coordinator,
        Guid taskId,
        string path)
    {
        LockDurationPolicy policy = LockDurationPolicy.Create(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromDays(1)).Value;
        FolderLockTask task = FolderLockTask.Create(
            FolderLockTaskId.Create(taskId).Value,
            FolderPath.Create(path).Value,
            LockDuration.Create(TimeSpan.FromHours(1), policy).Value,
            Now).Value;
        Assert.True(manager.Add(task).IsSuccess);
        Assert.True((await coordinator.ActivateAsync(task.Id)).IsSuccess);
    }

    private sealed class FailingScheduler : ILockTaskScheduler
    {
        public ValueTask<Result<int>> ProcessDueTasksAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<int>.Success(0));

        public ValueTask<Result> RunAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Failure(new Error(
                "lock_task.scheduler.loop.exception",
                "The lock task scheduler loop terminated unexpectedly.",
                ErrorCategory.PlatformError)));
    }

    private sealed class SuccessfulScheduler : ILockTaskScheduler
    {
        public ValueTask<Result<int>> ProcessDueTasksAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<int>.Success(0));

        public ValueTask<Result> RunAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());
    }

    private sealed class FailingFolderLockService : IFolderLockService
    {
        public ValueTask<Result<Guid>> CreateLockAsync(
            FolderLockRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<Guid>.Success(request.TaskId));

        public ValueTask<Result> RemoveLockAsync(
            Guid taskId,
            LockRemovalIntent intent,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Failure(taskId == FirstTaskId
                ? new Error(
                    "lock_task.cleanup.first",
                    "Sensitive first cleanup failure.",
                    ErrorCategory.UnrecoverableError)
                : new Error(
                    "lock_task.cleanup.later",
                    "Sensitive later cleanup failure.",
                    ErrorCategory.RecoverableError)));
    }

    private sealed class UnrelatedPathRelationService : IFolderPathRelationService
    {
        public FolderPathRelation GetRelation(
            FolderPath existingPath,
            FolderPath requestedPath) => FolderPathRelation.Unrelated;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 1;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingPlatform : IProtectedLogFilePlatform
    {
        internal List<byte> Bytes { get; } = [];
        internal int FlushCount { get; private set; }

        public Result<IProtectedLogFile> CreateNew(
            ProtectedLoggerMode mode,
            string leafName) => Result<IProtectedLogFile>.Success(new File(leafName));

        public Result Write(
            IProtectedLogFile file,
            ReadOnlyMemory<byte> bytes,
            long offset)
        {
            Assert.Equal(Bytes.Count, offset);
            Bytes.AddRange(bytes.ToArray());
            return Result.Success();
        }

        public Result Flush(IProtectedLogFile file)
        {
            FlushCount++;
            return Result.Success();
        }

        internal JsonElement[] ReadLines() => Encoding.UTF8
            .GetString(Bytes.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

        private sealed class File(string leafName) : IProtectedLogFile
        {
            public string LeafName { get; } = leafName;
            public void Dispose()
            {
            }
        }
    }
}
