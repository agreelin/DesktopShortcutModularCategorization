using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class WindowsRecoveryReadinessStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublishReadAndDelete_UseAtomicCanonicalSnapshotAndCleanTemporaryFiles()
    {
        await using Context context = Context.Create();
        RecoveryReadinessSnapshot first = Ready(sequence: 1);

        await context.Store.PublishAsync(first, default);
        RecoveryReadinessSnapshot read = await context.Store.ReadAsync(default);

        Assert.Equal(first, read);
        Assert.Equal(
            [WindowsRecoveryReadinessStore.CanonicalLeafName],
            Directory.EnumerateFiles(context.Root).Select(Path.GetFileName));

        RecoveryReadinessSnapshot second = first with
        {
            Sequence = 2,
            PublishedUtc = Now.AddSeconds(10),
            ValidUntilUtc = Now.AddSeconds(40),
        };
        context.Clock.UtcNow = Now.AddSeconds(10);
        await context.Store.PublishAsync(second, default);
        Assert.Equal(second, await context.Store.ReadAsync(default));
        Assert.Empty(Directory.EnumerateFiles(context.Root, "*.tmp-*"));

        await context.Store.DeleteAsync(default);
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Root));
        RecoveryReadinessException missing = await Assert.ThrowsAsync<RecoveryReadinessException>(
            () => context.Store.ReadAsync(default).AsTask());
        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_NOT_FOUND, missing.Code);
    }

    [Fact]
    public async Task Publish_RejectsSequenceRollbackAndCleansTheTemporaryHandle()
    {
        await using Context context = Context.Create();
        RecoveryReadinessSnapshot snapshot = Ready(sequence: 1);
        await context.Store.PublishAsync(snapshot, default);

        RecoveryReadinessException error = await Assert.ThrowsAsync<RecoveryReadinessException>(
            () => context.Store.PublishAsync(snapshot, default).AsTask());

        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID, error.Code);
        Assert.Equal(
            [WindowsRecoveryReadinessStore.CanonicalLeafName],
            Directory.EnumerateFiles(context.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Read_RejectsExpiredCanonicalSnapshot()
    {
        await using Context context = Context.Create();
        await context.Store.PublishAsync(Ready(sequence: 1), default);
        context.Clock.UtcNow = Now.AddSeconds(31);

        RecoveryReadinessException error = await Assert.ThrowsAsync<RecoveryReadinessException>(
            () => context.Store.ReadAsync(default).AsTask());

        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_STALE, error.Code);
    }

    [Fact]
    public async Task SecurityFailure_PreventsPublishAndDeletesTheSameTemporaryFile()
    {
        await using Context context = Context.Create(failTemporarySecurity: true);

        RecoveryReadinessException error = await Assert.ThrowsAsync<RecoveryReadinessException>(
            () => context.Store.PublishAsync(Ready(sequence: 1), default).AsTask());

        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Root));
    }

    private static RecoveryReadinessSnapshot Ready(long sequence) => new(
        1,
        "FolderSessionLockRecovery",
        Guid.Parse("11111111-2222-4333-8444-555555555555"),
        sequence,
        RecoveryReadinessState.Ready,
        false,
        Now,
        Now,
        Now,
        Now.AddSeconds(30),
        0,
        null);

    private sealed class Context : IAsyncDisposable
    {
        private Context(
            string root,
            MutableClock clock,
            WindowsRecoveryReadinessStore store)
        {
            Root = root;
            Clock = clock;
            Store = store;
        }

        internal string Root { get; }
        internal MutableClock Clock { get; }
        internal WindowsRecoveryReadinessStore Store { get; }

        internal static Context Create(bool failTemporarySecurity = false)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "FolderSessionLock.Tests",
                Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(root);
            var clock = new MutableClock(Now);
            var rawFiles = new WindowsRecoveryStoreFilePlatform();
            var files = new WindowsRecoveryReadinessFilePlatform(root, rawFiles);
            var security = new FakeSecurity(files, failTemporarySecurity);
            var store = new WindowsRecoveryReadinessStore(
                files,
                security,
                RecoveryReadinessMutex.CreateForTest(
                    $"Local\\FolderSessionLock.Tests.{Guid.NewGuid():N}"),
                clock);
            return new Context(root, clock, store);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSecurity(
        IRecoveryReadinessFilePlatform files,
        bool failTemporarySecurity) : IRecoveryReadinessFileSecurity
    {
        public ValueTask<Result<RecoveryRecordFileIdentity>> ApplyAndVerifyAsync(
            SafeFileHandle handle,
            RecoveryReadinessObjectKind kind,
            CancellationToken cancellationToken) =>
            failTemporarySecurity && kind == RecoveryReadinessObjectKind.TemporaryFile
                ? ValueTask.FromResult(Failure())
                : ValueTask.FromResult(files.GetIdentity(handle));

        public ValueTask<Result<RecoveryRecordFileIdentity>> VerifyAsync(
            SafeFileHandle handle,
            RecoveryReadinessObjectKind kind,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(files.GetIdentity(handle));

        private static Result<RecoveryRecordFileIdentity> Failure() =>
            Result<RecoveryRecordFileIdentity>.Failure(new Error(
                BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
                BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
                ErrorCategory.UnrecoverableError));
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
