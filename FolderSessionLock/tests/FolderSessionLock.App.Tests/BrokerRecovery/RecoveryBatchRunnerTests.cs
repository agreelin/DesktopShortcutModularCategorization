using System.Security.AccessControl;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class RecoveryBatchRunnerTests
{
    [Fact]
    public async Task RunAsync_ProcessesOrdinallyContinuesAfterFailureAndBuildsExactSummary()
    {
        string root = CreateRoot();
        try
        {
            Guid first = Guid.Parse("11111111-1111-4111-8111-111111111111");
            Guid second = Guid.Parse("22222222-2222-4222-8222-222222222222");
            Guid third = Guid.Parse("33333333-3333-4333-8333-333333333333");
            foreach (Guid id in new[] { third, first, second })
            {
                File.WriteAllBytes(Path.Combine(root, $"{id:D}.fslr"), []);
            }
            string backupPath = Path.Combine(root, $"{first:D}.bak");
            File.WriteAllBytes(backupPath, []);

            var cleanup = new FakeCleanup(root, new Dictionary<Guid, RecoveryRecordCleanupResult>
            {
                [first] = RecoveryRecordCleanupResult.Cleaned(first),
                [second] = RecoveryRecordCleanupResult.Failed(second, "FSL_E_SECOND"),
                [third] = RecoveryRecordCleanupResult.RecoveryRequired(third, "FSL_E_THIRD"),
            });
            var runner = Runner(root, cleanup, new TrustedVerifier());

            RecoveryRunSummary summary = await runner.RunAsync();

            Assert.Equal([first, second, third], cleanup.Calls);
            Assert.Equal(3, summary.canonicalRecordCount);
            Assert.Equal(3, summary.processedRecordCount);
            Assert.Equal(1, summary.cleanedCount);
            Assert.Equal(0, summary.alreadyCleanCount);
            Assert.Equal(1, summary.failedCount);
            Assert.Equal(1, summary.recoveryRequiredCount);
            Assert.Equal(0, summary.skippedCount);
            Assert.Equal(1, summary.auxiliaryArtifactCount);
            Assert.Equal(2, summary.remainingRecordCount);
            Assert.True(summary.recoveryBlocking);
            Assert.Equal("FSL_E_SECOND", summary.primaryErrorCode);
            Assert.True(File.Exists(backupPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotCleanupWhenPreflightOrLimitFails()
    {
        string root = CreateRoot();
        try
        {
            var blockedCleanup = new FakeCleanup(
                root,
                new Dictionary<Guid, RecoveryRecordCleanupResult>());
            RecoveryRunSummary preflight = await Runner(
                root,
                blockedCleanup,
                new BlockedVerifier()).RunAsync();
            Assert.Empty(blockedCleanup.Calls);
            Assert.Equal(BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH, preflight.primaryErrorCode);

            for (int index = 1; index <= 1025; index++)
            {
                File.WriteAllBytes(Path.Combine(root, $"{new Guid(index, 0, 0, new byte[8]):D}.fslr"), []);
            }

            var limitedCleanup = new FakeCleanup(
                root,
                new Dictionary<Guid, RecoveryRecordCleanupResult>());
            RecoveryRunSummary limited = await Runner(
                root,
                limitedCleanup,
                new TrustedVerifier()).RunAsync();
            Assert.Empty(limitedCleanup.Calls);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED, limited.primaryErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationSkipsUnstartedRecordsAndBlocks()
    {
        string root = CreateRoot();
        try
        {
            Guid id = Guid.Parse("11111111-1111-4111-8111-111111111111");
            File.WriteAllBytes(Path.Combine(root, $"{id:D}.fslr"), []);
            var cleanup = new FakeCleanup(
                root,
                new Dictionary<Guid, RecoveryRecordCleanupResult>());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            RecoveryRunSummary summary = await Runner(
                root,
                cleanup,
                new TrustedVerifier()).RunAsync(cancellation.Token);

            Assert.Empty(cleanup.Calls);
            Assert.Equal(1, summary.skippedCount);
            Assert.Equal(0, summary.processedRecordCount);
            Assert.True(summary.recoveryBlocking);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SummaryConstructor_RejectsBothCountInvariantViolations()
    {
        Assert.Throws<ArgumentException>(() => new RecoveryRunSummary(
            1, 1, 0, 0, 0, 0, 0, 0, 0, 1, true, null));
        Assert.Throws<ArgumentException>(() => new RecoveryRunSummary(
            2, 1, 1, 0, 0, 0, 0, 0, 0, 1, true, null));
        Assert.Throws<ArgumentException>(() => new RecoveryRunSummary(
            1, 0, 0, 0, 0, 0, 1, 0, 0, 1, false, null));
    }

    [Fact]
    public async Task RunAsync_RecordSecurityFailureDoesNotStopLaterRecords()
    {
        string root = CreateRoot();
        try
        {
            Guid first = Guid.Parse("11111111-1111-4111-8111-111111111111");
            Guid second = Guid.Parse("22222222-2222-4222-8222-222222222222");
            File.WriteAllBytes(Path.Combine(root, $"{first:D}.fslr"), []);
            File.WriteAllBytes(Path.Combine(root, $"{second:D}.fslr"), []);
            var cleanup = new FakeCleanup(
                root,
                new Dictionary<Guid, RecoveryRecordCleanupResult>());
            var fileSecurity = new FirstRecordSecurityFailure();

            RecoveryRunSummary summary = await Runner(
                root,
                cleanup,
                new TrustedVerifier(),
                fileSecurity).RunAsync();

            Assert.Equal([second], cleanup.Calls);
            Assert.Equal(1, summary.failedCount);
            Assert.Equal(1, summary.alreadyCleanCount);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_MISMATCH, summary.primaryErrorCode);
            Assert.True(File.Exists(Path.Combine(root, $"{first:D}.fslr")));
            Assert.False(File.Exists(Path.Combine(root, $"{second:D}.fslr")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RecoveryBatchRunner Runner(
        string root,
        RecoveryRecordAclCleanup cleanup,
        IProtectedPathSecurityVerifier verifier,
        IRecoveryRecordFileSecurity? fileSecurity = null)
    {
        var platform = new WindowsRecoveryStoreFilePlatform();
        return new RecoveryBatchRunner(
            verifier,
            [new(ProtectedPathKind.RecoveryRecordsDirectory, root)],
            new RecoveryDirectoryEnumerator(
                root,
                fileSecurity ?? new TrustedRecordSecurity(platform),
                platform),
            cleanup);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TrustedVerifier : IProtectedPathSecurityVerifier
    {
        public ValueTask<ProtectedPathSecurityCheckResult> VerifyAsync(
            ProtectedPathSecurityCheckRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProtectedPathSecurityCheckResult(true, null));
    }

    private sealed class BlockedVerifier : IProtectedPathSecurityVerifier
    {
        public ValueTask<ProtectedPathSecurityCheckResult> VerifyAsync(
            ProtectedPathSecurityCheckRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProtectedPathSecurityCheckResult(
                false,
                BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH));
    }

    private sealed class FakeCleanup : RecoveryRecordAclCleanup
    {
        private readonly string _root;
        private readonly IReadOnlyDictionary<Guid, RecoveryRecordCleanupResult> _results;

        internal FakeCleanup(
            string root,
            IReadOnlyDictionary<Guid, RecoveryRecordCleanupResult> results)
            : base(
                RecoveryTestData.CreateStore(root),
                new WindowsFolderPathValidator(new FolderPathSafetyPolicy(
                    Path.Combine(root, "repository"),
                    Path.Combine(root, "installation"),
                    [])),
                new FolderSessionLock.Windows.Security.DirectoryAclEditor(),
                new FixedClock())
        {
            _root = root;
            _results = results;
        }

        internal List<Guid> Calls { get; } = [];

        internal override ValueTask<RecoveryRecordCleanupResult> ExecuteAsync(
            Guid recoveryRecordId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ExecuteCore(recoveryRecordId));

        internal override ValueTask<RecoveryRecordCleanupResult> ExecuteAsync(
            RecoveryDirectoryRecord recoveryRecord,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(recoveryRecord.ErrorCode is null
                ? ExecuteCore(recoveryRecord.RecordId)
                : RecoveryRecordCleanupResult.Failed(
                    recoveryRecord.RecordId,
                    recoveryRecord.ErrorCode));

        private RecoveryRecordCleanupResult ExecuteCore(Guid recoveryRecordId)
        {
            Calls.Add(recoveryRecordId);
            RecoveryRecordCleanupResult result = _results.TryGetValue(recoveryRecordId, out var configured)
                ? configured
                : RecoveryRecordCleanupResult.AlreadyClean(recoveryRecordId);
            if (result.Disposition is RecoveryRecordCleanupDisposition.Cleaned
                or RecoveryRecordCleanupDisposition.AlreadyClean)
            {
                File.Delete(Path.Combine(_root, $"{recoveryRecordId:D}.fslr"));
            }

            return result;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class TrustedRecordSecurity(IRecoveryStoreFilePlatform platform)
        : IRecoveryRecordFileSecurity
    {
        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> ApplyAndVerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken) => VerifyAsync(fileHandle, fileKind, cancellationToken);

        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> VerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken)
        {
            Result<RecoveryRecordFileIdentity> identity = platform.GetIdentity(fileHandle);
            return ValueTask.FromResult(identity.IsSuccess
                ? Result<RecoveryRecordFileSecuritySnapshot>.Success(new(
                    fileKind,
                    identity.Value,
                    ProtectedPathAclPolicy.SystemSid,
                    true,
                    false,
                    true,
                    3))
                : Result<RecoveryRecordFileSecuritySnapshot>.Failure(identity.Error!));
        }
    }

    private sealed class FirstRecordSecurityFailure : IRecoveryRecordFileSecurity
    {
        private readonly WindowsRecoveryStoreFilePlatform _platform = new();
        private int _verifyCount;

        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> ApplyAndVerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken) => VerifyAsync(fileHandle, fileKind, cancellationToken);

        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> VerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _verifyCount) == 1)
            {
                return ValueTask.FromResult(Result<RecoveryRecordFileSecuritySnapshot>.Failure(new Error(
                    BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_MISMATCH,
                    BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_MISMATCH,
                    ErrorCategory.UnrecoverableError)));
            }

            return new TrustedRecordSecurity(_platform).VerifyAsync(
                fileHandle,
                fileKind,
                cancellationToken);
        }
    }
}
