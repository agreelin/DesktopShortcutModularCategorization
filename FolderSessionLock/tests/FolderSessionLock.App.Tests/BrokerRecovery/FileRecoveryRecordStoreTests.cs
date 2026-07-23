using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class FileRecoveryRecordStoreTests
{
    [Fact]
    public async Task WriteReadUpdateDelete_UsesExactAtomicRecordNamesAndLeavesNoArtifacts()
    {
        string directory = CreateTestDirectory();
        try
        {
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(directory);
            RecoveryRecord prepared = RecoveryTestData.Prepared();
            RecoveryRecord applied = RecoveryTestData.Applied();

            Result write = await store.WriteNewAsync(prepared);
            Assert.True(write.IsSuccess, write.Error?.Code);
            Assert.Equal(prepared, (await store.ReadAsync(prepared.RecordId)).Value);
            Assert.True((await store.UpdateAsync(applied)).IsSuccess);
            Assert.Equal(applied, (await store.ReadAsync(applied.RecordId)).Value);
            Assert.True((await store.DeleteAsync(applied)).IsSuccess);
            Assert.False(File.Exists(store.GetRecordPath(applied.RecordId)));
            Assert.Empty(Directory.EnumerateFiles(directory));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.bak"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateWriteNew_FailsWithoutReplacingLastVerifiedRecord()
    {
        string directory = CreateTestDirectory();
        try
        {
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(directory);
            RecoveryRecord prepared = RecoveryTestData.Prepared();

            Assert.True((await store.WriteNewAsync(prepared)).IsSuccess);
            Result duplicate = await store.WriteNewAsync(RecoveryTestData.Applied());

            Assert.True(duplicate.IsFailure);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_FILE_ALREADY_EXISTS, duplicate.Error!.Code);
            Assert.Equal(prepared, (await store.ReadAsync(prepared.RecordId)).Value);
            Assert.Single(Directory.EnumerateFiles(directory, "*.fslr"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.bak"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptRecordRead_FailsWithoutDeletingOrOverwritingSource()
    {
        string directory = CreateTestDirectory();
        try
        {
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(directory);
            RecoveryRecord record = RecoveryTestData.Prepared();
            string path = store.GetRecordPath(record.RecordId);
            File.WriteAllBytes(path, [0, 1, 2]);

            Result<RecoveryRecord> result = await store.ReadAsync(record.RecordId);

            Assert.True(result.IsFailure);
            Assert.Equal("FSL_E_RECOVERY_RECORD_TRUNCATED", result.Error!.Code);
            Assert.Equal([0, 1, 2], File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporarySecurityFailure_HappensBeforePayloadWriteAndCleansTemporaryFile()
    {
        string directory = CreateTestDirectory();
        try
        {
            var platform = new RecordingFilePlatform();
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: new ApplyFailureSecurity(
                    BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_SET_FAILED));

            Result result = await store.WriteNewAsync(RecoveryTestData.Prepared());

            Assert.True(result.IsFailure);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_SET_FAILED, result.Error!.Code);
            Assert.Equal(0, platform.WriteCallCount);
            Assert.Equal(1, platform.DeleteCallCount);
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryCleanupFailure_OverridesOriginalErrorAndPermanentlyBlocksWrites()
    {
        string directory = CreateTestDirectory();
        try
        {
            var safetyState = new RecoveryStoreWriteSafetyState();
            var platform = new RecordingFilePlatform
            {
                DeleteErrorCode = BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED,
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: new ApplyFailureSecurity(
                    BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_SET_FAILED),
                writeSafetyState: safetyState);

            Result first = await store.WriteNewAsync(RecoveryTestData.Prepared());
            Result second = await store.WriteNewAsync(RecoveryTestData.Prepared());

            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_TEMP_CLEANUP_FAILED, first.Error!.Code);
            Assert.True(safetyState.IsWriteBlocked);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_TEMP_CLEANUP_FAILED, safetyState.BlockingErrorCode);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_TEMP_CLEANUP_FAILED, second.Error!.Code);
            Assert.Equal(0, platform.WriteCallCount);
            Assert.Single(Directory.EnumerateFiles(directory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryCreatedThenCancelledBeforeSecurity_CleansAndRethrowsCancellation()
    {
        string directory = CreateTestDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var platform = new RecordingFilePlatform
            {
                AfterCreateTemporary = cancellation.Cancel,
            };
            var security = new ControlledFileSecurity(platform);
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: security);

            OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await store.WriteNewAsync(
                    RecoveryTestData.Prepared(),
                    cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(0, security.ApplyCallCount);
            Assert.True(platform.CreatedTemporaryHandle!.IsClosed);
            Assert.Equal(1, platform.DeleteCallCount);
            Assert.Equal(1, platform.CloseCallCount);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporarySecurityCancellation_CleansAndRethrowsOriginalCancellation()
    {
        string directory = CreateTestDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var platform = new RecordingFilePlatform();
            var security = new ControlledFileSecurity(
                platform,
                new OperationCanceledException(cancellation.Token));
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: security);

            OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await store.WriteNewAsync(
                    RecoveryTestData.Prepared(),
                    cancellation.Token));

            Assert.Same(security.Exception, exception);
            Assert.Equal(1, security.ApplyCallCount);
            Assert.True(platform.CreatedTemporaryHandle!.IsClosed);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporarySecurityException_CleansAndRethrowsOriginalException()
    {
        string directory = CreateTestDirectory();
        try
        {
            var injected = new InvalidOperationException("injected security failure");
            var platform = new RecordingFilePlatform();
            var security = new ControlledFileSecurity(platform, injected);
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: security);

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await store.WriteNewAsync(RecoveryTestData.Prepared()));

            Assert.Same(injected, exception);
            Assert.True(platform.CreatedTemporaryHandle!.IsClosed);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryRenameException_CleansAndRethrowsOriginalException()
    {
        string directory = CreateTestDirectory();
        try
        {
            var injected = new InvalidOperationException("injected rename failure");
            var platform = new RecordingFilePlatform { RenameException = injected };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await store.WriteNewAsync(RecoveryTestData.Prepared()));

            Assert.Same(injected, exception);
            Assert.True(platform.CreatedTemporaryHandle!.IsClosed);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryCleanupDispositionFailure_OverridesCancellationAndBlocksWrites()
    {
        string directory = CreateTestDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var safetyState = new RecoveryStoreWriteSafetyState();
            var platform = new RecordingFilePlatform
            {
                DeleteErrorCode = BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED,
            };
            var security = new ControlledFileSecurity(
                platform,
                new OperationCanceledException(cancellation.Token));
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: security,
                writeSafetyState: safetyState);

            Result result = await store.WriteNewAsync(
                RecoveryTestData.Prepared(),
                cancellation.Token);

            AssertTemporaryCleanupFailure(result, safetyState, platform);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryCleanupCloseFailure_OverridesOriginalFailureAndBlocksWrites()
    {
        string directory = CreateTestDirectory();
        try
        {
            var safetyState = new RecoveryStoreWriteSafetyState();
            var platform = new RecordingFilePlatform
            {
                CloseErrorCode = BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: new ApplyFailureSecurity(
                    BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_SET_FAILED),
                writeSafetyState: safetyState);

            Result result = await store.WriteNewAsync(RecoveryTestData.Prepared());

            AssertTemporaryCleanupFailure(result, safetyState, platform);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryCleanupVisibleLeafAfterClose_BlocksWrites()
    {
        string directory = CreateTestDirectory();
        try
        {
            var safetyState = new RecoveryStoreWriteSafetyState();
            var platform = new RecordingFilePlatform
            {
                LeafIdentityAfterClose = new RecoveryRecordFileIdentity(1, 2, 3, 1),
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: new ApplyFailureSecurity(
                    BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_SET_FAILED),
                writeSafetyState: safetyState);

            Result result = await store.WriteNewAsync(RecoveryTestData.Prepared());

            AssertTemporaryCleanupFailure(result, safetyState, platform);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryCleanupEnumerationFailure_BlocksWrites()
    {
        string directory = CreateTestDirectory();
        try
        {
            var safetyState = new RecoveryStoreWriteSafetyState();
            var platform = new RecordingFilePlatform
            {
                LeafIdentityErrorAfterClose = BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED,
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: new ApplyFailureSecurity(
                    BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_SET_FAILED),
                writeSafetyState: safetyState);

            Result result = await store.WriteNewAsync(RecoveryTestData.Prepared());

            AssertTemporaryCleanupFailure(result, safetyState, platform);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryCleanupDirectoryIdentityChange_BlocksWrites()
    {
        string directory = CreateTestDirectory();
        try
        {
            var safetyState = new RecoveryStoreWriteSafetyState();
            var platform = new RecordingFilePlatform
            {
                ChangeDirectoryIdentityAfterClose = true,
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: new ApplyFailureSecurity(
                    BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_SET_FAILED),
                writeSafetyState: safetyState);

            Result result = await store.WriteNewAsync(RecoveryTestData.Prepared());

            AssertTemporaryCleanupFailure(result, safetyState, platform);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED)]
    [InlineData(BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED)]
    public async Task Update_RenameFailureKeepsExistingRecordAndCleansTemporaryFile(string errorCode)
    {
        string directory = CreateTestDirectory();
        try
        {
            RecoveryRecord prepared = RecoveryTestData.Prepared();
            FileRecoveryRecordStore initial = RecoveryTestData.CreateStore(directory);
            Assert.True((await initial.WriteNewAsync(prepared)).IsSuccess);
            var platform = new RecordingFilePlatform { RenameErrorCode = errorCode };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            Result result = await store.UpdateAsync(RecoveryTestData.Applied());

            Assert.Equal(errorCode, result.Error!.Code);
            Assert.Equal(prepared, (await initial.ReadAsync(prepared.RecordId)).Value);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.bak"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PostCommitFailure_ReturnsUnifiedErrorAndRetainsCommittedRecord()
    {
        string directory = CreateTestDirectory();
        try
        {
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                testHook: new ThrowingCommitHook(RecoveryRecordCommitPoint.AfterAtomicCommit));
            RecoveryRecord record = RecoveryTestData.Prepared();

            Result result = await store.WriteNewAsync(record);

            Assert.Equal(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED,
                result.Error!.Code);
            Assert.True(File.Exists(store.GetRecordPath(record.RecordId)));
            Assert.Equal(record, (await RecoveryTestData.CreateStore(directory).ReadAsync(record.RecordId)).Value);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostCommitHookCancellation_ReturnsUnifiedErrorAndRetainsCommittedRecord(
        bool afterAtomicCommit)
    {
        string directory = CreateTestDirectory();
        try
        {
            var platform = new RecordingFilePlatform();
            RecoveryRecordCommitPoint point = afterAtomicCommit
                ? RecoveryRecordCommitPoint.AfterAtomicCommit
                : RecoveryRecordCommitPoint.AfterFinalVerification;
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                testHook: new ThrowingCommitHook(point, new OperationCanceledException()),
                filePlatform: platform);
            RecoveryRecord record = RecoveryTestData.Prepared();

            Result result = await store.WriteNewAsync(record);

            await AssertPostCommitFailureAsync(result, store, record, platform);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PostCommitCallerCancellation_DoesNotCancelFinalVerification()
    {
        string directory = CreateTestDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var platform = new RecordingFilePlatform();
            var security = new PostCommitFileSecurity(platform);
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                testHook: new CancelingCommitHook(cancellation),
                filePlatform: platform,
                fileSecurity: security);
            RecoveryRecord record = RecoveryTestData.Prepared();

            Result result = await store.WriteNewAsync(record, cancellation.Token);

            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(CancellationToken.None, security.PostCommitCancellationToken);
            Assert.True(File.Exists(store.GetRecordPath(record.RecordId)));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
            Assert.Equal(0, platform.DeleteCallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostCommitFileSecurityException_ReturnsUnifiedErrorAndRetainsCommittedRecord(
        bool cancellationException)
    {
        string directory = CreateTestDirectory();
        try
        {
            var platform = new RecordingFilePlatform();
            Exception exception = cancellationException
                ? new OperationCanceledException()
                : new InvalidOperationException("Injected post-commit verification failure.");
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                fileSecurity: new PostCommitFileSecurity(platform, exception));
            RecoveryRecord record = RecoveryTestData.Prepared();

            Result result = await store.WriteNewAsync(record);

            await AssertPostCommitFailureAsync(result, store, record, platform);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PostCommitLeafMappingMismatch_ReturnsUnifiedErrorAndRetainsCommittedRecord()
    {
        string directory = CreateTestDirectory();
        try
        {
            var platform = new RecordingFilePlatform { ReturnMissingLeafIdentity = true };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);
            RecoveryRecord record = RecoveryTestData.Prepared();

            Result result = await store.WriteNewAsync(record);

            Assert.Equal(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED,
                result.Error!.Code);
            Assert.True(File.Exists(store.GetRecordPath(record.RecordId)));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(BrokerErrorCodes.FSL_E_RECOVERY_FILE_HANDLE_DELETE_UNSUPPORTED)]
    [InlineData(BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED)]
    public async Task DeleteFailure_ReturnsExactErrorAndLeavesCanonicalRecord(string errorCode)
    {
        string directory = CreateTestDirectory();
        try
        {
            RecoveryRecord record = RecoveryTestData.Prepared();
            FileRecoveryRecordStore initial = RecoveryTestData.CreateStore(directory);
            Assert.True((await initial.WriteNewAsync(record)).IsSuccess);
            var platform = new RecordingFilePlatform { DeleteErrorCode = errorCode };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            Result result = await store.DeleteAsync(record);

            Assert.Equal(errorCode, result.Error!.Code);
            Assert.True(File.Exists(store.GetRecordPath(record.RecordId)));
            Assert.Equal(record, (await initial.ReadAsync(record.RecordId)).Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_ClosesCanonicalHandleBeforeCheckingLeafDisappearance()
    {
        string directory = CreateTestDirectory();
        try
        {
            RecoveryRecord record = await WriteRecordAsync(directory);
            var platform = new RecordingFilePlatform();
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            Result result = await store.DeleteAsync(record);

            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.True(platform.HandleWasOpenAtClose);
            Assert.True(platform.ClosedHandleWasClosedBeforeLeafCheck);
            Assert.Equal(["Delete", "CloseAfterDisposition", "GetLeafIdentityAfterClose"], platform.DeleteOrder);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_CloseCannotBeProvenReturnsRecoveryRequiredWithoutEnumeration()
    {
        string directory = CreateTestDirectory();
        try
        {
            RecoveryRecord record = await WriteRecordAsync(directory);
            var platform = new RecordingFilePlatform
            {
                CloseErrorCode = BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED,
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            Result result = await store.DeleteAsync(record);

            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED, result.Error!.Code);
            Assert.Equal(1, platform.DeleteCallCount);
            Assert.Equal(1, platform.CloseCallCount);
            Assert.Equal(0, platform.PostCloseLeafIdentityCallCount);
            Assert.False(File.Exists(store.GetRecordPath(record.RecordId)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_NameStillVisibleAfterCloseReturnsRecoveryRequiredWithoutSecondDelete()
    {
        string directory = CreateTestDirectory();
        try
        {
            RecoveryRecord record = await WriteRecordAsync(directory);
            var platform = new RecordingFilePlatform
            {
                LeafIdentityAfterClose = new RecoveryRecordFileIdentity(1, 2, 3, 1),
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            Result result = await store.DeleteAsync(record);

            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED, result.Error!.Code);
            Assert.Equal(1, platform.DeleteCallCount);
            Assert.Equal(1, platform.CloseCallCount);
            Assert.Equal(1, platform.PostCloseLeafIdentityCallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_EnumerationFailureAfterCloseReturnsRecoveryRequired()
    {
        string directory = CreateTestDirectory();
        try
        {
            RecoveryRecord record = await WriteRecordAsync(directory);
            var platform = new RecordingFilePlatform
            {
                LeafIdentityErrorAfterClose = BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED,
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            Result result = await store.DeleteAsync(record);

            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED, result.Error!.Code);
            Assert.Equal(1, platform.DeleteCallCount);
            Assert.Equal(1, platform.CloseCallCount);
            Assert.Equal(1, platform.PostCloseLeafIdentityCallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_DirectoryIdentityChangeAfterCloseReturnsRecoveryRequired()
    {
        string directory = CreateTestDirectory();
        try
        {
            RecoveryRecord record = await WriteRecordAsync(directory);
            var platform = new RecordingFilePlatform
            {
                ChangeDirectoryIdentityAfterClose = true,
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            Result result = await store.DeleteAsync(record);

            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED, result.Error!.Code);
            Assert.Equal(1, platform.DirectoryIdentityCheckAfterCloseCount);
            Assert.False(File.Exists(store.GetRecordPath(record.RecordId)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_ReplacementAppearingAfterDispositionIsNotDeleted()
    {
        string directory = CreateTestDirectory();
        try
        {
            RecoveryRecord record = await WriteRecordAsync(directory);
            string path = Path.Combine(directory, $"{record.RecordId:D}.fslr");
            var platform = new RecordingFilePlatform
            {
                ReplacementPathAfterClose = path,
                ReplacementBytes = [7, 8, 9],
            };
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            Result result = await store.DeleteAsync(record);

            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED, result.Error!.Code);
            Assert.Equal(1, platform.DeleteCallCount);
            Assert.Equal(1, platform.CloseCallCount);
            Assert.Equal([7, 8, 9], File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_SuccessRequiresAbsentNameAndStableDirectoryIdentity()
    {
        string directory = CreateTestDirectory();
        try
        {
            RecoveryRecord record = await WriteRecordAsync(directory);
            var platform = new RecordingFilePlatform();
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform);

            Result result = await store.DeleteAsync(record);

            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Equal(1, platform.DeleteCallCount);
            Assert.Equal(1, platform.CloseCallCount);
            Assert.Equal(1, platform.PostCloseLeafIdentityCallCount);
            Assert.Equal(1, platform.DirectoryIdentityCheckAfterCloseCount);
            Assert.False(File.Exists(store.GetRecordPath(record.RecordId)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreBlockedSafetyState_RejectsWriteBeforeOpeningOrCreatingFiles()
    {
        string directory = CreateTestDirectory();
        try
        {
            var safetyState = new RecoveryStoreWriteSafetyState();
            safetyState.BlockWrites(BrokerErrorCodes.FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED);
            var platform = new RecordingFilePlatform();
            FileRecoveryRecordStore store = RecoveryTestData.CreateStore(
                directory,
                filePlatform: platform,
                writeSafetyState: safetyState);

            Result result = await store.WriteNewAsync(RecoveryTestData.Prepared());

            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED, result.Error!.Code);
            Assert.Equal(0, platform.OpenDirectoryCallCount);
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TestStore_RejectsPathsOutsideGuidTemporaryRoot()
    {
        var platform = new WindowsRecoveryStoreFilePlatform();
        Assert.Throws<ArgumentException>(() => FileRecoveryRecordStore.CreateForTest(
            Path.GetTempPath(),
            new TrustedProtectedPathVerifier(),
            new TrustedFileSecurity(platform),
            platform,
            RecoveryStoreMutex.CreateForTest("FolderSessionLock.Tests.InvalidPath"),
            new RecoveryStoreWriteSafetyState()));
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

    private static async Task<RecoveryRecord> WriteRecordAsync(string directory)
    {
        RecoveryRecord record = RecoveryTestData.Prepared();
        FileRecoveryRecordStore store = RecoveryTestData.CreateStore(directory);
        Result write = await store.WriteNewAsync(record);
        Assert.True(write.IsSuccess, write.Error?.Code);
        return record;
    }

    private static void AssertTemporaryCleanupFailure(
        Result result,
        RecoveryStoreWriteSafetyState safetyState,
        RecordingFilePlatform platform)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_TEMP_CLEANUP_FAILED, result.Error!.Code);
        Assert.True(safetyState.IsWriteBlocked);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_RECOVERY_TEMP_CLEANUP_FAILED,
            safetyState.BlockingErrorCode);
        Assert.Equal(1, platform.DeleteCallCount);
        Assert.Equal(1, platform.CloseCallCount);
        Assert.True(platform.CreatedTemporaryHandle!.IsClosed);
    }

    private static async Task AssertPostCommitFailureAsync(
        Result result,
        FileRecoveryRecordStore store,
        RecoveryRecord record,
        RecordingFilePlatform platform)
    {
        Assert.Equal(
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED,
            result.Error!.Code);
        Assert.True(File.Exists(store.GetRecordPath(record.RecordId)));
        Assert.Equal(
            record,
            (await RecoveryTestData.CreateStore(store.RecordsDirectory).ReadAsync(record.RecordId)).Value);
        Assert.Empty(Directory.EnumerateFiles(store.RecordsDirectory, "*.tmp-*"));
        Assert.Equal(0, platform.DeleteCallCount);
        Assert.True(platform.CreatedTemporaryHandle!.IsClosed);
    }

    private static Error Error(string code) => new(
        code,
        code,
        ErrorCategory.UnrecoverableError);

    private sealed class ThrowingCommitHook(
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

    private sealed class CancelingCommitHook(CancellationTokenSource cancellation)
        : IFileRecoveryRecordStoreTestHook
    {
        public void OnCommitPoint(
            RecoveryRecordCommitPoint current,
            string temporaryPath,
            string finalPath,
            string backupPath)
        {
            if (current == RecoveryRecordCommitPoint.AfterAtomicCommit)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class ApplyFailureSecurity(string errorCode) : IRecoveryRecordFileSecurity
    {
        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> ApplyAndVerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                Result<RecoveryRecordFileSecuritySnapshot>.Failure(Error(errorCode)));

        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> VerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Verification must not run after the injected apply failure.");
    }

    private sealed class ControlledFileSecurity(
        IRecoveryStoreFilePlatform platform,
        Exception? exception = null) : IRecoveryRecordFileSecurity
    {
        internal Exception? Exception { get; } = exception;
        internal int ApplyCallCount { get; private set; }

        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> ApplyAndVerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken)
        {
            ApplyCallCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            return VerifyAsync(fileHandle, fileKind, cancellationToken);
        }

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

    private sealed class PostCommitFileSecurity(
        IRecoveryStoreFilePlatform platform,
        Exception? postCommitException = null) : IRecoveryRecordFileSecurity
    {
        private int _verifyCallCount;

        internal CancellationToken PostCommitCancellationToken { get; private set; }

        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> ApplyAndVerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken) => Snapshot(fileHandle, fileKind);

        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> VerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken)
        {
            _verifyCallCount++;
            if (_verifyCallCount == 2)
            {
                PostCommitCancellationToken = cancellationToken;
                if (postCommitException is not null)
                {
                    throw postCommitException;
                }
            }

            return Snapshot(fileHandle, fileKind);
        }

        private ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> Snapshot(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind)
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

    private sealed class TrustedProtectedPathVerifier : IProtectedPathSecurityVerifier
    {
        public ValueTask<ProtectedPathSecurityCheckResult> VerifyAsync(
            ProtectedPathSecurityCheckRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new ProtectedPathSecurityCheckResult(true, null));
    }

    private sealed class TrustedFileSecurity(IRecoveryStoreFilePlatform platform)
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

    private sealed class RecordingFilePlatform : IRecoveryStoreFilePlatform
    {
        private readonly WindowsRecoveryStoreFilePlatform _inner = new();

        internal int OpenDirectoryCallCount { get; private set; }
        internal int WriteCallCount { get; private set; }
        internal int DeleteCallCount { get; private set; }
        internal int CloseCallCount { get; private set; }
        internal int PostCloseLeafIdentityCallCount { get; private set; }
        internal int DirectoryIdentityCheckAfterCloseCount { get; private set; }
        internal bool HandleWasOpenAtClose { get; private set; }
        internal bool ClosedHandleWasClosedBeforeLeafCheck { get; private set; }
        internal List<string> DeleteOrder { get; } = [];
        internal string? RenameErrorCode { get; init; }
        internal Exception? RenameException { get; init; }
        internal string? DeleteErrorCode { get; init; }
        internal string? CloseErrorCode { get; init; }
        internal string? LeafIdentityErrorAfterClose { get; init; }
        internal RecoveryRecordFileIdentity? LeafIdentityAfterClose { get; init; }
        internal bool ChangeDirectoryIdentityAfterClose { get; init; }
        internal string? ReplacementPathAfterClose { get; init; }
        internal byte[] ReplacementBytes { get; init; } = [];
        internal bool ReturnMissingLeafIdentity { get; init; }
        internal Action? AfterCreateTemporary { get; init; }
        internal SafeFileHandle? CreatedTemporaryHandle { get; private set; }
        internal string? CreatedTemporaryLeaf { get; private set; }
        private nint _directoryHandle;
        private SafeFileHandle? _closedCanonicalHandle;

        public Result<SafeFileHandle> OpenDirectory(string path)
        {
            OpenDirectoryCallCount++;
            Result<SafeFileHandle> open = _inner.OpenDirectory(path);
            if (open.IsSuccess)
            {
                _directoryHandle = open.Value.DangerousGetHandle();
            }

            return open;
        }

        public Result<SafeFileHandle> CreateTemporary(
            SafeFileHandle directoryHandle,
            string leafName)
        {
            Result<SafeFileHandle> create = _inner.CreateTemporary(directoryHandle, leafName);
            if (create.IsSuccess)
            {
                CreatedTemporaryHandle = create.Value;
                CreatedTemporaryLeaf = leafName;
                AfterCreateTemporary?.Invoke();
            }

            return create;
        }

        public Result<SafeFileHandle> OpenExisting(
            SafeFileHandle directoryHandle,
            string leafName) => _inner.OpenExisting(directoryHandle, leafName);

        public Result<RecoveryRecordFileIdentity> GetIdentity(SafeFileHandle handle)
        {
            Result<RecoveryRecordFileIdentity> identity = _inner.GetIdentity(handle);
            if (identity.IsSuccess
                && CloseCallCount > 0
                && handle.DangerousGetHandle() == _directoryHandle)
            {
                DirectoryIdentityCheckAfterCloseCount++;
                if (ChangeDirectoryIdentityAfterClose)
                {
                    return Result<RecoveryRecordFileIdentity>.Success(identity.Value with
                    {
                        FileIdLow = identity.Value.FileIdLow ^ 1,
                    });
                }
            }

            return identity;
        }

        public Result<NativeMethods.FileAttributeTagInfo> GetAttributes(SafeFileHandle handle) =>
            _inner.GetAttributes(handle);

        public Result<string> GetFinalPath(SafeFileHandle handle) => _inner.GetFinalPath(handle);

        public Result WriteAll(SafeFileHandle handle, ReadOnlyMemory<byte> bytes)
        {
            WriteCallCount++;
            return _inner.WriteAll(handle, bytes);
        }

        public Result Flush(SafeFileHandle handle) => _inner.Flush(handle);

        public Result<byte[]> ReadAll(SafeFileHandle handle, int maximumLength) =>
            _inner.ReadAll(handle, maximumLength);

        public Result Rename(
            SafeFileHandle fileHandle,
            SafeFileHandle directoryHandle,
            string targetLeafName,
            bool replaceExisting)
        {
            if (RenameException is not null)
            {
                throw RenameException;
            }

            return RenameErrorCode is null
                ? _inner.Rename(fileHandle, directoryHandle, targetLeafName, replaceExisting)
                : Result.Failure(Error(RenameErrorCode));
        }

        public Result Delete(SafeFileHandle fileHandle)
        {
            DeleteCallCount++;
            DeleteOrder.Add("Delete");
            return DeleteErrorCode is null
                ? _inner.Delete(fileHandle)
                : Result.Failure(Error(DeleteErrorCode));
        }

        public Result CloseAfterDisposition(SafeFileHandle fileHandle)
        {
            CloseCallCount++;
            DeleteOrder.Add("CloseAfterDisposition");
            HandleWasOpenAtClose = !fileHandle.IsClosed;
            _closedCanonicalHandle = fileHandle;
            Result close = _inner.CloseAfterDisposition(fileHandle);
            if (ReplacementPathAfterClose is not null)
            {
                File.WriteAllBytes(ReplacementPathAfterClose, ReplacementBytes);
            }

            return CloseErrorCode is null
                ? close
                : Result.Failure(Error(CloseErrorCode));
        }

        public Result<RecoveryRecordFileIdentity?> GetLeafIdentity(
            SafeFileHandle directoryHandle,
            string leafName)
        {
            if (CloseCallCount > 0)
            {
                PostCloseLeafIdentityCallCount++;
                DeleteOrder.Add("GetLeafIdentityAfterClose");
                ClosedHandleWasClosedBeforeLeafCheck = _closedCanonicalHandle?.IsClosed == true;
                if (LeafIdentityErrorAfterClose is not null)
                {
                    return Result<RecoveryRecordFileIdentity?>.Failure(
                        Error(LeafIdentityErrorAfterClose));
                }

                if (LeafIdentityAfterClose is not null)
                {
                    return Result<RecoveryRecordFileIdentity?>.Success(LeafIdentityAfterClose);
                }
            }

            return ReturnMissingLeafIdentity
                ? Result<RecoveryRecordFileIdentity?>.Success(null)
                : _inner.GetLeafIdentity(directoryHandle, leafName);
        }
    }
}
