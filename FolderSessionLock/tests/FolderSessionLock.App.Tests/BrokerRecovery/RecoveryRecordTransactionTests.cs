using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class RecoveryRecordTransactionTests
{
    [Fact]
    public async Task Transaction_PersistsPreparedAppliedCleanupPendingFailedAndDelete()
    {
        string directory = CreateTestDirectory();
        try
        {
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(directory);
            var registry = new RecoveryTaskRegistry();
            var clock = new TransactionClock(new DateTimeOffset(2026, 7, 19, 16, 30, 0, TimeSpan.Zero));
            var transaction = new RecoveryRecordTransaction(store, registry, clock);
            var request = new FolderLockRequest(
                Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                @"C:\Data\Locked",
                TimeSpan.FromHours(2));
            var identity = new SessionIdentity(
                "S-1-5-21-1000-1001-1002-1003",
                "S-1-5-5-1-2",
                1);
            string targetPath = Path.Combine(directory, "target");
            Directory.CreateDirectory(targetPath);
            var validator = new WindowsFolderPathValidator(new FolderPathSafetyPolicy(
                Path.Combine(directory, "repository"),
                Path.Combine(directory, "installation"),
                []));
            using ValidatedDirectory validated = validator.Validate(targetPath).Value;
            request = request with { FolderPath = validated.NormalizedPath };
            var preparedEvidence = new RecoveryAclEvidence(
                RecoveryTestData.AceHash,
                RecoveryTestData.BaselineHash,
                null);

            var prepare = await transaction.PrepareAsync(
                request,
                identity,
                validated,
                preparedEvidence,
                CancellationToken.None);
            Assert.True(prepare.IsSuccess, prepare.Error?.Code);
            RecoveryRecord prepared = (await store.ReadAsync(prepare.Value)).Value;
            Assert.Equal(RecoveryRecordState.Prepared, prepared.State);
            Assert.Null(prepared.PostApplyDaclSha256);

            clock.Advance(TimeSpan.FromSeconds(1));
            var appliedEvidence = preparedEvidence with { PostApplyDaclSha256 = RecoveryTestData.PostApplyHash };
            Assert.True((await transaction.MarkAppliedAsync(
                prepare.Value,
                appliedEvidence,
                CancellationToken.None)).IsSuccess);
            Assert.Equal(RecoveryRecordState.Applied, (await store.ReadAsync(prepare.Value)).Value.State);

            clock.Advance(TimeSpan.FromHours(2));
            Assert.True((await transaction.MarkCleanupPendingAsync(
                prepare.Value,
                CancellationToken.None)).IsSuccess);
            RecoveryRecord pending = (await store.ReadAsync(prepare.Value)).Value;
            Assert.Equal(RecoveryRecordState.CleanupPending, pending.State);
            Assert.Equal(1, pending.CleanupAttemptCount);

            Assert.True((await transaction.MarkCleanupFailedAsync(
                prepare.Value,
                new FolderSessionLock.Core.Results.Error(
                    "FSL_E_ACL_REMOVE_FAILED",
                    "sensitive details are not persisted",
                    FolderSessionLock.Core.Results.ErrorCategory.UnrecoverableError),
                CancellationToken.None)).IsSuccess);
            RecoveryRecord failed = (await store.ReadAsync(prepare.Value)).Value;
            Assert.Equal(RecoveryRecordState.CleanupFailed, failed.State);
            Assert.Equal("FSL_E_ACL_REMOVE_FAILED", failed.LastErrorCode);
            Assert.Equal("FSL_E_ACL_REMOVE_FAILED", failed.LastErrorMessage);

            Assert.True((await transaction.DeleteAsync(
                prepare.Value,
                CancellationToken.None)).IsSuccess);
            Assert.Null(registry.GetByRecordId(prepare.Value));
            Assert.False(File.Exists(store.GetRecordPath(prepare.Value)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Prepare_AtomicCommitInterruptionLeavesPreparedRecordForRecovery()
    {
        using TransactionContext context = TransactionContext.Create(
            RecoveryRecordCommitPoint.AfterAtomicCommit);

        Result<Guid> result = await context.Transaction.PrepareAsync(
            context.Request,
            context.Identity,
            context.Validated,
            context.PreparedEvidence,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        string recordPath = Assert.Single(Directory.EnumerateFiles(context.RecordsDirectory, "*.fslr"));
        Guid recordId = Guid.ParseExact(Path.GetFileNameWithoutExtension(recordPath), "D");
        RecoveryRecord record = (await context.Store.ReadAsync(recordId)).Value;
        Assert.Equal(RecoveryRecordState.Prepared, record.State);
        Assert.Null(context.Registry.GetByRecordId(recordId));
    }

    [Fact]
    public async Task Prepare_AtomicCommitCancellationReturnsFailureAndLeavesPreparedRecordForRecovery()
    {
        using TransactionContext context = TransactionContext.Create(
            RecoveryRecordCommitPoint.AfterAtomicCommit,
            new OperationCanceledException());

        Result<Guid> result = await context.Transaction.PrepareAsync(
            context.Request,
            context.Identity,
            context.Validated,
            context.PreparedEvidence,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED,
            result.Error!.Code);
        string recordPath = Assert.Single(Directory.EnumerateFiles(context.RecordsDirectory, "*.fslr"));
        Guid recordId = Guid.ParseExact(Path.GetFileNameWithoutExtension(recordPath), "D");
        RecoveryRecord record = (await context.Store.ReadAsync(recordId)).Value;
        Assert.Equal(RecoveryRecordState.Prepared, record.State);
        Assert.Null(context.Registry.GetByRecordId(recordId));
    }

    [Fact]
    public async Task MarkApplied_InterruptionKeepsPreparedRecordAndRegisteredResponsibility()
    {
        using TransactionContext context = TransactionContext.Create();
        Guid recordId = await context.PrepareAsync();
        FileRecoveryRecordStore interruptedStore = RecoveryTestData.CreateStore(
            context.RecordsDirectory,
            testHook: new InterruptingCommitHook(RecoveryRecordCommitPoint.AfterTemporaryVerification));
        var interrupted = new RecoveryRecordTransaction(
            interruptedStore,
            context.Registry,
            context.Clock);
        context.Clock.Advance(TimeSpan.FromSeconds(1));

        Result result = await interrupted.MarkAppliedAsync(
            recordId,
            context.PreparedEvidence with { PostApplyDaclSha256 = RecoveryTestData.PostApplyHash },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(RecoveryRecordState.Prepared, (await context.Store.ReadAsync(recordId)).Value.State);
        Assert.Equal(RecoveryRecordState.Prepared, context.Registry.GetByRecordId(recordId)!.State);
    }

    [Fact]
    public async Task MarkCleanupPending_InterruptionKeepsAppliedRecordAndRegisteredResponsibility()
    {
        using TransactionContext context = TransactionContext.Create();
        Guid recordId = await context.PrepareAsync();
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await context.Transaction.MarkAppliedAsync(
            recordId,
            context.PreparedEvidence with { PostApplyDaclSha256 = RecoveryTestData.PostApplyHash },
            CancellationToken.None)).IsSuccess);
        FileRecoveryRecordStore interruptedStore = RecoveryTestData.CreateStore(
            context.RecordsDirectory,
            testHook: new InterruptingCommitHook(RecoveryRecordCommitPoint.AfterTemporaryVerification));
        var interrupted = new RecoveryRecordTransaction(
            interruptedStore,
            context.Registry,
            context.Clock);
        context.Clock.Advance(TimeSpan.FromSeconds(1));

        Result result = await interrupted.MarkCleanupPendingAsync(recordId, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(RecoveryRecordState.Applied, (await context.Store.ReadAsync(recordId)).Value.State);
        Assert.Equal(RecoveryRecordState.Applied, context.Registry.GetByRecordId(recordId)!.State);
    }

    [Fact]
    public async Task Delete_FailureKeepsAppliedRecordAndRegisteredResponsibility()
    {
        using TransactionContext context = TransactionContext.Create();
        Guid recordId = await context.PrepareAsync();
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await context.Transaction.MarkAppliedAsync(
            recordId,
            context.PreparedEvidence with { PostApplyDaclSha256 = RecoveryTestData.PostApplyHash },
            CancellationToken.None)).IsSuccess);
        FileRecoveryRecordStore failingStore = RecoveryTestData.CreateStore(
            context.RecordsDirectory,
            filePlatform: new DeleteFailingFilePlatform());
        var failingTransaction = new RecoveryRecordTransaction(
            failingStore,
            context.Registry,
            context.Clock);

        Result result = await failingTransaction.DeleteAsync(recordId, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED, result.Error!.Code);
        Assert.Equal(RecoveryRecordState.Applied, (await context.Store.ReadAsync(recordId)).Value.State);
        Assert.Equal(RecoveryRecordState.Applied, context.Registry.GetByRecordId(recordId)!.State);
    }

    [Fact]
    public async Task RecoveryAclCleanup_ExecuteRemovesAppliedAceAndDeletesRecord()
    {
        await using RecoveryAclCleanupContext context = await RecoveryAclCleanupContext.Create();
        string backupPath = Path.Combine(
            context.Store.RecordsDirectory,
            $"{context.RecordId:D}.bak");
        var cleanup = new RecoveryRecordAclCleanup(
            context.Store,
            context.Validator,
            context.Editor,
            context.Clock,
            new BackupCreatingCleanupHook(backupPath));
        context.Clock.Advance(TimeSpan.FromSeconds(1));

        RecoveryRecordCleanupResult result = await cleanup.ExecuteAsync(context.RecordId);

        Assert.Equal(RecoveryRecordCleanupDisposition.Cleaned, result.Disposition);
        Assert.Null(result.ErrorCode);
        Assert.Equal(0, context.CountTargetAces());
        Assert.False(File.Exists(context.Store.GetRecordPath(context.RecordId)));
        Assert.True(File.Exists(backupPath));
        string probe = Path.Combine(context.TargetPath, "probe.txt");
        File.WriteAllText(probe, "probe");
        Assert.Equal("probe", File.ReadAllText(probe));
        File.Delete(probe);
    }

    [Fact]
    public async Task RecoveryAclCleanup_AlreadyCleanDeletesOnlyCanonicalRecord()
    {
        await using RecoveryAclCleanupContext context = await RecoveryAclCleanupContext.Create();
        string backupPath = Path.Combine(
            context.Store.RecordsDirectory,
            $"{context.RecordId:D}.bak");
        Assert.True(context.Editor.RemoveDenyAce(
            context.Validated.Handle,
            context.Operation).IsSuccess);
        var cleanup = new RecoveryRecordAclCleanup(
            context.Store,
            context.Validator,
            context.Editor,
            context.Clock,
            new BackupCreatingCleanupHook(backupPath));
        context.Clock.Advance(TimeSpan.FromSeconds(1));

        RecoveryRecordCleanupResult result = await cleanup.ExecuteAsync(context.RecordId);

        Assert.Equal(RecoveryRecordCleanupDisposition.AlreadyClean, result.Disposition);
        Assert.False(File.Exists(context.Store.GetRecordPath(context.RecordId)));
        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public async Task RecoveryAclCleanup_DaclDriftStopsWithoutRemovingOrDeleting()
    {
        await using RecoveryAclCleanupContext context = await RecoveryAclCleanupContext.Create();
        SecurityIdentifier worldSid = new(WellKnownSidType.WorldSid, null);
        DirectoryAclOperation? driftOperation = null;

        try
        {
            Result<DirectoryAclOperation> drift = context.Editor.AddDenyAce(
                context.Validated.Handle,
                worldSid,
                out driftOperation);
            Assert.True(drift.IsSuccess, drift.Error?.Code);
            context.Clock.Advance(TimeSpan.FromSeconds(1));
            DirectoryAclSnapshot beforeCleanup = context.Editor.ReadSnapshot(
                context.Validated.Handle).Value;

            RecoveryRecordCleanupResult result = await context.Cleanup.ExecuteAsync(context.RecordId);

            Assert.Equal(RecoveryRecordCleanupDisposition.Failed, result.Disposition);
            Assert.Equal(BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH, result.ErrorCode);
            Assert.Equal(1, context.CountTargetAces());
            Assert.True(DirectoryAclEditor.SnapshotsEqual(
                beforeCleanup,
                context.Editor.ReadSnapshot(context.Validated.Handle).Value));
            Assert.True(File.Exists(context.Store.GetRecordPath(context.RecordId)));
            RecoveryRecord failed = (await context.Store.ReadAsync(context.RecordId)).Value;
            Assert.Equal(RecoveryRecordState.CleanupFailed, failed.State);
            Assert.Equal(1, failed.CleanupAttemptCount);
            Assert.Equal(BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH, failed.LastErrorCode);
            Assert.Equal(BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH, failed.LastErrorMessage);
        }
        finally
        {
            if (driftOperation is not null)
            {
                Result cleanup = context.Editor.RemoveDenyAce(
                    context.Validated.Handle,
                    driftOperation);
                if (cleanup.IsFailure)
                {
                    RecoveryAclCleanupSafetyGate.Block(cleanup.Error!);
                    throw new InvalidOperationException(cleanup.Error!.Code);
                }
            }
        }
    }

    [Fact]
    public async Task RecoveryAclCleanup_IdempotentBaselineRejectsFingerprintMismatch()
    {
        await using RecoveryAclCleanupContext context = await RecoveryAclCleanupContext.Create();
        RecoveryRecord record = (await context.Store.ReadAsync(context.RecordId)).Value;
        Assert.True((await context.Store.UpdateAsync(record with
        {
            AceFingerprintSha256 = new string('a', 64),
        })).IsSuccess);
        Assert.True(context.Editor.RemoveDenyAce(
            context.Validated.Handle,
            context.Operation).IsSuccess);
        context.Clock.Advance(TimeSpan.FromSeconds(1));

        RecoveryRecordCleanupResult result = await context.Cleanup.ExecuteAsync(context.RecordId);

        Assert.Equal(RecoveryRecordCleanupDisposition.Failed, result.Disposition);
        Assert.Equal(BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH, result.ErrorCode);
        Assert.Equal(0, context.CountTargetAces());
        Assert.True(File.Exists(context.Store.GetRecordPath(context.RecordId)));
    }

    [Fact]
    public async Task RecoveryAclCleanup_PathReplacementLeavesReplacementAclUnchanged()
    {
        await using RecoveryAclCleanupContext context = await RecoveryAclCleanupContext.Create();
        string originalPath = Path.Combine(context.Root, "original-target");
        var hook = new ReplacingCleanupHook(
            context.TargetPath,
            originalPath,
            context.Validator,
            context.Editor);
        var cleanup = new RecoveryRecordAclCleanup(
            context.Store,
            context.Validator,
            context.Editor,
            context.Clock,
            hook);
        context.Clock.Advance(TimeSpan.FromSeconds(1));

        RecoveryRecordCleanupResult result = await cleanup.ExecuteAsync(context.RecordId);

        Assert.Equal(RecoveryRecordCleanupDisposition.Failed, result.Disposition);
        Assert.Equal(BrokerErrorCodes.FSL_E_PATH_IDENTITY_CHANGED, result.ErrorCode);
        Assert.Equal(0, context.CountTargetAces());
        Assert.True(File.Exists(context.Store.GetRecordPath(context.RecordId)));
        using ValidatedDirectory replacement = context.Validator.Validate(context.TargetPath).Value;
        DirectoryAclSnapshot replacementAfter = context.Editor.ReadSnapshot(replacement.Handle).Value;
        Assert.True(DirectoryAclEditor.SnapshotsEqual(hook.ReplacementBefore!, replacementAfter));
    }

    [Fact]
    public async Task RecoveryAclCleanup_CorruptRecordDoesNotAccessPathOrDeleteRecord()
    {
        string root = CreateTestDirectory();
        try
        {
            string records = Path.Combine(root, "records");
            Directory.CreateDirectory(records);
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(records);
            Guid recordId = Guid.NewGuid();
            File.WriteAllBytes(store.GetRecordPath(recordId), [1, 2, 3]);
            var platform = new CountingPathPlatform();
            var validator = new WindowsFolderPathValidator(CreatePolicy(root), platform);
            var cleanup = new RecoveryRecordAclCleanup(
                store,
                validator,
                new DirectoryAclEditor(),
                new TransactionClock(DateTimeOffset.UtcNow));

            RecoveryRecordCleanupResult result = await cleanup.ExecuteAsync(recordId);

            Assert.Equal(RecoveryRecordCleanupDisposition.Failed, result.Disposition);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_TRUNCATED, result.ErrorCode);
            Assert.Equal(0, platform.OpenPathCalls);
            Assert.True(File.Exists(store.GetRecordPath(recordId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryAclCleanup_UnsupportedMaskDoesNotAccessPathOrDeleteRecord()
    {
        string root = CreateTestDirectory();
        try
        {
            string records = Path.Combine(root, "records");
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(records);
            DateTimeOffset created = new(2026, 7, 20, 1, 0, 0, TimeSpan.Zero);
            var record = new RecoveryRecord(
                1,
                "1.0",
                Guid.NewGuid(),
                Guid.NewGuid(),
                RecoveryRecordState.Applied,
                Path.GetFullPath(target),
                1,
                2,
                3,
                "S-1-5-21-1000-1001-1002-1003",
                "S-1-5-5-1-2",
                1,
                AccessControlType.Deny,
                1,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                created,
                created.AddHours(1),
                created.AddSeconds(1),
                0,
                null,
                null);
            Assert.True((await store.WriteNewAsync(record)).IsSuccess);
            var platform = new CountingPathPlatform();
            var cleanup = new RecoveryRecordAclCleanup(
                store,
                new WindowsFolderPathValidator(CreatePolicy(root), platform),
                new DirectoryAclEditor(),
                new TransactionClock(created.AddSeconds(2)));

            RecoveryRecordCleanupResult result = await cleanup.ExecuteAsync(record.RecordId);

            Assert.Equal(RecoveryRecordCleanupDisposition.Failed, result.Disposition);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_ACCESS_MASK_UNSUPPORTED, result.ErrorCode);
            Assert.Equal(0, platform.OpenPathCalls);
            Assert.True(File.Exists(store.GetRecordPath(record.RecordId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryAclCleanup_UsesTheExactRecordIdMismatchCodeBeforePathAccess()
    {
        string root = CreateTestDirectory();
        try
        {
            string records = Path.Combine(root, "records");
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(records);
            RecoveryRecord record = RecoveryTestData.Applied() with { RecordId = Guid.NewGuid() };
            Assert.True((await store.WriteNewAsync(record)).IsSuccess);
            Guid fileId = Guid.NewGuid();
            File.Move(store.GetRecordPath(record.RecordId), store.GetRecordPath(fileId));
            var platform = new CountingPathPlatform();
            var cleanup = new RecoveryRecordAclCleanup(
                store,
                new WindowsFolderPathValidator(CreatePolicy(root), platform),
                new DirectoryAclEditor(),
                new TransactionClock(DateTimeOffset.UtcNow));

            RecoveryRecordCleanupResult result = await cleanup.ExecuteAsync(fileId);

            Assert.Equal(RecoveryRecordCleanupDisposition.Failed, result.Disposition);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_ID_MISMATCH, result.ErrorCode);
            Assert.Equal(0, platform.OpenPathCalls);
            Assert.True(File.Exists(store.GetRecordPath(fileId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryAclCleanup_CancellationBeforePendingSkipsButAfterPendingCompletesCriticalSection()
    {
        await using RecoveryAclCleanupContext beforeContext = await RecoveryAclCleanupContext.Create();
        using var beforeCancellation = new CancellationTokenSource();
        var beforeHook = new CancellingCleanupHook(beforeCancellation, cancelAfterPending: false);
        var beforeCleanup = new RecoveryRecordAclCleanup(
            beforeContext.Store,
            beforeContext.Validator,
            beforeContext.Editor,
            beforeContext.Clock,
            beforeHook);

        RecoveryRecordCleanupResult before = await beforeCleanup.ExecuteAsync(
            beforeContext.RecordId,
            beforeCancellation.Token);

        Assert.Equal(RecoveryRecordCleanupDisposition.Skipped, before.Disposition);
        Assert.Equal(1, beforeContext.CountTargetAces());
        Assert.True(File.Exists(beforeContext.Store.GetRecordPath(beforeContext.RecordId)));

        await using RecoveryAclCleanupContext afterContext = await RecoveryAclCleanupContext.Create();
        using var afterCancellation = new CancellationTokenSource();
        var afterHook = new CancellingCleanupHook(afterCancellation, cancelAfterPending: true);
        var afterCleanup = new RecoveryRecordAclCleanup(
            afterContext.Store,
            afterContext.Validator,
            afterContext.Editor,
            afterContext.Clock,
            afterHook);

        RecoveryRecordCleanupResult after = await afterCleanup.ExecuteAsync(
            afterContext.RecordId,
            afterCancellation.Token);

        Assert.Equal(RecoveryRecordCleanupDisposition.Cleaned, after.Disposition);
        Assert.Equal(0, afterContext.CountTargetAces());
        Assert.False(File.Exists(afterContext.Store.GetRecordPath(afterContext.RecordId)));
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static FolderPathSafetyPolicy CreatePolicy(string root) => new(
        Path.Combine(root, "repository"),
        Path.Combine(root, "installation"),
        []);

    private sealed class TransactionClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        internal void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class InterruptingCommitHook(
        RecoveryRecordCommitPoint point,
        Exception? exception = null)
        : IFileRecoveryRecordStoreTestHook
    {
        public void OnCommitPoint(
            RecoveryRecordCommitPoint current,
            string temporaryPath,
            string finalPath,
            string backupPath)
        {
            if (current == point)
            {
                throw exception ?? new IOException($"Injected interruption at {current}.");
            }
        }
    }

    private sealed class CountingPathPlatform : WindowsFolderPathPlatform
    {
        internal int OpenPathCalls { get; private set; }

        internal override uint GetDriveType(string rootPath) => NativeMethods.DriveFixed;

        internal override Result<SafeFileHandle> OpenPath(string path, uint desiredAccess)
        {
            OpenPathCalls++;
            return Result<SafeFileHandle>.Failure(new Error(
                "windows.path.not_found",
                "windows.path.not_found",
                ErrorCategory.ValidationFailed));
        }
    }

    private sealed class DeleteFailingFilePlatform : IRecoveryStoreFilePlatform
    {
        private readonly WindowsRecoveryStoreFilePlatform _inner = new();

        public Result<SafeFileHandle> OpenDirectory(string path) => _inner.OpenDirectory(path);

        public Result<SafeFileHandle> CreateTemporary(
            SafeFileHandle directoryHandle,
            string leafName) => _inner.CreateTemporary(directoryHandle, leafName);

        public Result<SafeFileHandle> OpenExisting(
            SafeFileHandle directoryHandle,
            string leafName) => _inner.OpenExisting(directoryHandle, leafName);

        public Result<RecoveryRecordFileIdentity> GetIdentity(SafeFileHandle handle) =>
            _inner.GetIdentity(handle);

        public Result<NativeMethods.FileAttributeTagInfo> GetAttributes(SafeFileHandle handle) =>
            _inner.GetAttributes(handle);

        public Result<string> GetFinalPath(SafeFileHandle handle) => _inner.GetFinalPath(handle);

        public Result WriteAll(SafeFileHandle handle, ReadOnlyMemory<byte> bytes) =>
            _inner.WriteAll(handle, bytes);

        public Result Flush(SafeFileHandle handle) => _inner.Flush(handle);

        public Result<byte[]> ReadAll(SafeFileHandle handle, int maximumLength) =>
            _inner.ReadAll(handle, maximumLength);

        public Result Rename(
            SafeFileHandle fileHandle,
            SafeFileHandle directoryHandle,
            string targetLeafName,
            bool replaceExisting) => _inner.Rename(
                fileHandle,
                directoryHandle,
                targetLeafName,
                replaceExisting);

        public Result Delete(SafeFileHandle fileHandle) => Result.Failure(new Error(
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED,
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED,
            ErrorCategory.UnrecoverableError));

        public Result CloseAfterDisposition(SafeFileHandle fileHandle) =>
            _inner.CloseAfterDisposition(fileHandle);

        public Result<RecoveryRecordFileIdentity?> GetLeafIdentity(
            SafeFileHandle directoryHandle,
            string leafName) => _inner.GetLeafIdentity(directoryHandle, leafName);
    }

    private sealed class ReplacingCleanupHook(
        string targetPath,
        string originalPath,
        WindowsFolderPathValidator validator,
        DirectoryAclEditor editor) : IRecoveryRecordAclCleanupTestHook
    {
        internal DirectoryAclSnapshot? ReplacementBefore { get; private set; }

        public void BeforeAclCleanup(ValidatedDirectory directory)
        {
            Directory.Move(targetPath, originalPath);
            Directory.CreateDirectory(targetPath);
            using ValidatedDirectory replacement = validator.Validate(targetPath).Value;
            ReplacementBefore = editor.ReadSnapshot(replacement.Handle).Value;
        }
    }

    private sealed class CancellingCleanupHook(
        CancellationTokenSource cancellation,
        bool cancelAfterPending) : IRecoveryRecordAclCleanupTestHook
    {
        public void BeforeAclCleanup(ValidatedDirectory directory)
        {
            if (!cancelAfterPending)
            {
                cancellation.Cancel();
            }
        }

        public void AfterCleanupPending(ValidatedDirectory directory)
        {
            if (cancelAfterPending)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class BackupCreatingCleanupHook(string backupPath)
        : IRecoveryRecordAclCleanupTestHook
    {
        public void BeforeAclCleanup(ValidatedDirectory directory)
        {
        }

        public void AfterCleanupPending(ValidatedDirectory directory) =>
            File.WriteAllBytes(backupPath, []);
    }

    private static class RecoveryAclCleanupSafetyGate
    {
        private static Exception? _failure;

        internal static void EnsureCanWrite()
        {
            if (_failure is not null)
            {
                throw new InvalidOperationException(
                    "A prior recovery ACL cleanup failed; further ACL writes are blocked.",
                    _failure);
            }
        }

        internal static void Block(object failure) => _failure = failure switch
        {
            Exception exception => exception,
            _ => new InvalidOperationException(failure.ToString()),
        };
    }

    private sealed class RecoveryAclCleanupContext : IAsyncDisposable
    {
        private RecoveryAclCleanupContext(
            string root,
            string targetPath,
            FileRecoveryRecordStore store,
            TransactionClock clock,
            WindowsFolderPathValidator validator,
            DirectoryAclEditor editor,
            ValidatedDirectory validated,
            DirectoryAclOperation operation,
            Guid recordId)
        {
            Root = root;
            TargetPath = targetPath;
            Store = store;
            Clock = clock;
            Validator = validator;
            Editor = editor;
            Validated = validated;
            Operation = operation;
            RecordId = recordId;
            Cleanup = new RecoveryRecordAclCleanup(store, validator, editor, clock);
        }

        internal string Root { get; }
        internal string TargetPath { get; }
        internal FileRecoveryRecordStore Store { get; }
        internal TransactionClock Clock { get; }
        internal WindowsFolderPathValidator Validator { get; }
        internal DirectoryAclEditor Editor { get; }
        internal ValidatedDirectory Validated { get; }
        internal DirectoryAclOperation Operation { get; }
        internal Guid RecordId { get; }
        internal RecoveryRecordAclCleanup Cleanup { get; }

        internal static async Task<RecoveryAclCleanupContext> Create()
        {
            RecoveryAclCleanupSafetyGate.EnsureCanWrite();
            string root = CreateTestDirectory();
            string targetPath = Path.Combine(root, "target");
            Directory.CreateDirectory(targetPath);
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(Path.Combine(root, "records"));
            var clock = new TransactionClock(
                new DateTimeOffset(2026, 7, 20, 1, 0, 0, TimeSpan.Zero));
            var validator = new WindowsFolderPathValidator(CreatePolicy(root));
            ValidatedDirectory validated = validator.Validate(targetPath).Value;
            var editor = new DirectoryAclEditor();
            Result<SessionIdentity> identity =
                await new WindowsSessionIdentityProvider().GetCurrentAsync();
            Assert.True(identity.IsSuccess, identity.Error?.Code);
            SecurityIdentifier logonSid = new(identity.Value.LogonSid);
            DirectoryAclPreparation preparation = editor.PrepareDenyAce(
                validated.Handle,
                logonSid).Value;
            var registry = new RecoveryTaskRegistry();
            var transaction = new RecoveryRecordTransaction(store, registry, clock);
            var request = new FolderLockRequest(
                Guid.NewGuid(),
                validated.NormalizedPath,
                TimeSpan.FromHours(1));
            Guid recordId = (await transaction.PrepareAsync(
                request,
                identity.Value,
                validated,
                preparation.Evidence,
                CancellationToken.None)).Value;
            Result<DirectoryAclOperation> add = editor.ApplyPreparedDenyAce(
                validated.Handle,
                preparation,
                out DirectoryAclOperation? operation);
            Assert.True(add.IsSuccess, add.Error?.Code);
            operation = Assert.IsType<DirectoryAclOperation>(operation);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True((await transaction.MarkAppliedAsync(
                recordId,
                operation.Evidence,
                CancellationToken.None)).IsSuccess);
            return new RecoveryAclCleanupContext(
                root,
                targetPath,
                store,
                clock,
                validator,
                editor,
                validated,
                operation,
                recordId);
        }

        internal int CountTargetAces() => Editor.ReadSnapshot(Validated.Handle).Value.AceBinaries
            .Count(ace => ace.AsSpan().SequenceEqual(Operation.AceBinary));

        public async ValueTask DisposeAsync()
        {
            try
            {
                Result remove = Editor.RemoveDenyAce(Validated.Handle, Operation);
                if (remove.IsFailure)
                {
                    throw new InvalidOperationException(remove.Error!.Code);
                }

                if (File.Exists(Store.GetRecordPath(RecordId)))
                {
                    Result<RecoveryRecord> read = await Store.ReadAsync(RecordId);
                    if (read.IsFailure)
                    {
                        throw new InvalidOperationException(read.Error!.Code);
                    }

                    Result delete = await Store.DeleteAsync(read.Value);
                    if (delete.IsFailure)
                    {
                        throw new InvalidOperationException(delete.Error!.Code);
                    }
                }

                Validated.Dispose();
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception)
            {
                RecoveryAclCleanupSafetyGate.Block(exception);
                throw;
            }
        }
    }

    private sealed class TransactionContext : IDisposable
    {
        private TransactionContext(
            string root,
            string recordsDirectory,
            FileRecoveryRecordStore store,
            RecoveryTaskRegistry registry,
            TransactionClock clock,
            RecoveryRecordTransaction transaction,
            ValidatedDirectory validated,
            FolderLockRequest request,
            SessionIdentity identity,
            RecoveryAclEvidence preparedEvidence)
        {
            Root = root;
            RecordsDirectory = recordsDirectory;
            Store = store;
            Registry = registry;
            Clock = clock;
            Transaction = transaction;
            Validated = validated;
            Request = request;
            Identity = identity;
            PreparedEvidence = preparedEvidence;
        }

        internal string Root { get; }
        internal string RecordsDirectory { get; }
        internal FileRecoveryRecordStore Store { get; }
        internal RecoveryTaskRegistry Registry { get; }
        internal TransactionClock Clock { get; }
        internal RecoveryRecordTransaction Transaction { get; }
        internal ValidatedDirectory Validated { get; }
        internal FolderLockRequest Request { get; }
        internal SessionIdentity Identity { get; }
        internal RecoveryAclEvidence PreparedEvidence { get; }

        internal static TransactionContext Create(
            RecoveryRecordCommitPoint? interruptAt = null,
            Exception? interruption = null)
        {
            string root = CreateTestDirectory();
            string recordsDirectory = Path.Combine(root, "records");
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                recordsDirectory,
                testHook: interruptAt is null
                    ? null
                    : new InterruptingCommitHook(interruptAt.Value, interruption));
            var registry = new RecoveryTaskRegistry();
            var clock = new TransactionClock(
                new DateTimeOffset(2026, 7, 19, 16, 30, 0, TimeSpan.Zero));
            var transaction = new RecoveryRecordTransaction(store, registry, clock);
            string targetPath = Path.Combine(root, "target");
            Directory.CreateDirectory(targetPath);
            var validator = new WindowsFolderPathValidator(new FolderPathSafetyPolicy(
                Path.Combine(root, "repository"),
                Path.Combine(root, "installation"),
                []));
            ValidatedDirectory validated = validator.Validate(targetPath).Value;
            var request = new FolderLockRequest(
                Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                validated.NormalizedPath,
                TimeSpan.FromHours(2));
            var identity = new SessionIdentity(
                "S-1-5-21-1000-1001-1002-1003",
                "S-1-5-5-1-2",
                1);
            var evidence = new RecoveryAclEvidence(
                RecoveryTestData.AceHash,
                RecoveryTestData.BaselineHash,
                null);
            return new TransactionContext(
                root,
                recordsDirectory,
                store,
                registry,
                clock,
                transaction,
                validated,
                request,
                identity,
                evidence);
        }

        internal async ValueTask<Guid> PrepareAsync()
        {
            Result<Guid> result = await Transaction.PrepareAsync(
                Request,
                Identity,
                Validated,
                PreparedEvidence,
                CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error?.Code);
            return result.Value;
        }

        public void Dispose()
        {
            Validated.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
