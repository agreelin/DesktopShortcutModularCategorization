using System.Security.Cryptography;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery;

internal sealed class FileRecoveryRecordStore
{
    private readonly string _recordsDirectory;
    private readonly RecoveryRecordContainer _container;
    private readonly IFileRecoveryRecordStoreTestHook? _testHook;
    private readonly bool _createDirectory;
    private readonly IProtectedPathSecurityVerifier _protectedPathVerifier;
    private readonly ProtectedPathSecurityCheckRequest _protectedPathRequest;
    private readonly IRecoveryRecordFileSecurity _fileSecurity;
    private readonly IRecoveryStoreFilePlatform _filePlatform;
    private readonly IRecoveryStoreMutex _mutex;
    private readonly IRecoveryStoreWriteSafetyState _writeSafetyState;

    private FileRecoveryRecordStore(
        string recordsDirectory,
        RecoveryRecordContainer container,
        IProtectedPathSecurityVerifier protectedPathVerifier,
        IRecoveryRecordFileSecurity fileSecurity,
        IRecoveryStoreFilePlatform filePlatform,
        IRecoveryStoreMutex mutex,
        IRecoveryStoreWriteSafetyState writeSafetyState,
        IFileRecoveryRecordStoreTestHook? testHook,
        bool createDirectory)
    {
        _recordsDirectory = recordsDirectory;
        _container = container;
        _protectedPathVerifier = protectedPathVerifier;
        _protectedPathRequest = new(
            ProtectedPathKind.RecoveryRecordsDirectory,
            recordsDirectory);
        _fileSecurity = fileSecurity;
        _filePlatform = filePlatform;
        _mutex = mutex;
        _writeSafetyState = writeSafetyState;
        _testHook = testHook;
        _createDirectory = createDirectory;
    }

    internal static FileRecoveryRecordStore CreateProduction(
        ProtectedPathSet pathSet,
        IRecoveryStoreWriteSafetyState writeSafetyState)
    {
        ArgumentNullException.ThrowIfNull(pathSet);
        return new FileRecoveryRecordStore(
            pathSet.RecoveryRecordsDirectory,
            new RecoveryRecordContainer(),
            new WindowsProtectedPathSecurityVerifier(pathSet),
            new WindowsRecoveryRecordFileSecurity(),
            new WindowsRecoveryStoreFilePlatform(),
            RecoveryStoreMutex.CreateProduction(),
            writeSafetyState ?? throw new ArgumentNullException(nameof(writeSafetyState)),
            null,
            createDirectory: false);
    }

    internal static FileRecoveryRecordStore CreateForTest(
        string recordsDirectory,
        IProtectedPathSecurityVerifier protectedPathVerifier,
        IRecoveryRecordFileSecurity fileSecurity,
        IRecoveryStoreFilePlatform filePlatform,
        IRecoveryStoreMutex mutex,
        IRecoveryStoreWriteSafetyState writeSafetyState,
        RecoveryRecordContainer? container = null,
        IFileRecoveryRecordStoreTestHook? testHook = null)
    {
        string fullPath = Path.GetFullPath(recordsDirectory);
        string testRoot = Path.Combine(Path.GetTempPath(), "FolderSessionLock.Tests");
        string relative = Path.GetRelativePath(testRoot, fullPath);
        string[] components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || components.Length == 0
            || !Guid.TryParseExact(components[0], "D", out _))
        {
            throw new ArgumentException(
                "Recovery record tests require a FolderSessionLock.Tests GUID directory.",
                nameof(recordsDirectory));
        }

        return new FileRecoveryRecordStore(
            fullPath,
            container ?? new RecoveryRecordContainer(),
            protectedPathVerifier ?? throw new ArgumentNullException(nameof(protectedPathVerifier)),
            fileSecurity ?? throw new ArgumentNullException(nameof(fileSecurity)),
            filePlatform ?? throw new ArgumentNullException(nameof(filePlatform)),
            mutex ?? throw new ArgumentNullException(nameof(mutex)),
            writeSafetyState ?? throw new ArgumentNullException(nameof(writeSafetyState)),
            testHook,
            createDirectory: true);
    }

    internal ValueTask<Result> WriteNewAsync(
        RecoveryRecord record,
        CancellationToken cancellationToken = default) =>
        CommitAsync(record, isUpdate: false, cancellationToken);

    internal ValueTask<Result> UpdateAsync(
        RecoveryRecord record,
        CancellationToken cancellationToken = default) =>
        CommitAsync(record, isUpdate: true, cancellationToken);

    internal async ValueTask<Result<RecoveryRecord>> ReadAsync(
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        Result<StoreContext> contextResult = await OpenContextAsync(
            writing: false,
            cancellationToken);
        if (contextResult.IsFailure)
        {
            return Result<RecoveryRecord>.Failure(contextResult.Error!);
        }

        using StoreContext context = contextResult.Value;
        return await ReadByLeafAsync(
            context,
            recordId,
            expectedIdentity: null,
            expectedRecord: null,
            cancellationToken);
    }

    internal async ValueTask<Result<RecoveryRecord>> ReadAsync(
        RecoveryDirectoryRecord record,
        CancellationToken cancellationToken = default)
    {
        if (record.ErrorCode is not null || record.FileIdentity is null)
        {
            return Result<RecoveryRecord>.Failure(Error(
                record.ErrorCode ?? BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH));
        }

        Result<StoreContext> contextResult = await OpenContextAsync(
            writing: false,
            cancellationToken);
        if (contextResult.IsFailure)
        {
            return Result<RecoveryRecord>.Failure(contextResult.Error!);
        }

        using StoreContext context = contextResult.Value;
        return await ReadByLeafAsync(
            context,
            record.RecordId,
            record.FileIdentity,
            expectedRecord: null,
            cancellationToken);
    }

    internal async ValueTask<Result> DeleteCanonicalRecordAsync(
        RecoveryRecord expectedRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        Result<StoreContext> contextResult = await OpenContextAsync(
            writing: true,
            cancellationToken);
        if (contextResult.IsFailure)
        {
            return Result.Failure(contextResult.Error!);
        }

        using StoreContext context = contextResult.Value;
        string leaf = CanonicalLeaf(expectedRecord.RecordId);
        Result<SafeFileHandle> open = _filePlatform.OpenExisting(context.DirectoryHandle, leaf);
        if (open.IsFailure)
        {
            return Result.Failure(Normalize(open.Error!));
        }

        SafeFileHandle? canonicalHandle = open.Value;
        try
        {
            Result<ValidatedRecord> initial = await ValidateRecordAsync(
                context,
                canonicalHandle,
                leaf,
                RecoveryRecordFileKind.CanonicalRecord,
                expectedIdentity: null,
                expectedRecord,
                cancellationToken);
            if (initial.IsFailure)
            {
                return Result.Failure(initial.Error!);
            }

            Result mapping = VerifyLeafMapping(context, leaf, initial.Value.Identity);
            if (mapping.IsFailure)
            {
                return mapping;
            }

            Result<ValidatedRecord> final = await ValidateRecordAsync(
                context,
                canonicalHandle,
                leaf,
                RecoveryRecordFileKind.CanonicalRecord,
                initial.Value.Identity,
                expectedRecord,
                cancellationToken);
            if (final.IsFailure)
            {
                return Result.Failure(final.Error!);
            }

            Result delete = _filePlatform.Delete(canonicalHandle);
            if (delete.IsFailure)
            {
                return Result.Failure(Normalize(delete.Error!));
            }

            Result close = _filePlatform.CloseAfterDisposition(canonicalHandle);
            canonicalHandle = null;
            if (close.IsFailure)
            {
                return Result.Failure(Error(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED));
            }

            Result<RecoveryRecordFileIdentity?> remaining = _filePlatform.GetLeafIdentity(
                context.DirectoryHandle,
                leaf);
            if (remaining.IsFailure || remaining.Value is not null)
            {
                return Result.Failure(Error(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED));
            }

            Result directoryIdentity = VerifyDirectoryIdentity(context);
            return directoryIdentity.IsSuccess
                ? Result.Success()
                : Result.Failure(Error(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED));
        }
        finally
        {
            canonicalHandle?.Dispose();
        }
    }

    internal ValueTask<Result> DeleteAsync(
        RecoveryRecord expectedRecord,
        CancellationToken cancellationToken = default) =>
        DeleteCanonicalRecordAsync(expectedRecord, cancellationToken);

    internal string GetRecordPath(Guid recordId) =>
        Path.Combine(_recordsDirectory, CanonicalLeaf(recordId));

    internal string RecordsDirectory => _recordsDirectory;

    private async ValueTask<Result> CommitAsync(
        RecoveryRecord record,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Result<StoreContext> contextResult = await OpenContextAsync(
            writing: true,
            cancellationToken);
        if (contextResult.IsFailure)
        {
            return Result.Failure(contextResult.Error!);
        }

        using StoreContext context = contextResult.Value;
        string canonicalLeaf = CanonicalLeaf(record.RecordId);
        SafeFileHandle? oldHandle = null;
        ValidatedRecord? oldRecord = null;
        if (isUpdate)
        {
            Result<SafeFileHandle> oldOpen = _filePlatform.OpenExisting(
                context.DirectoryHandle,
                canonicalLeaf);
            if (oldOpen.IsFailure)
            {
                return Result.Failure(Normalize(oldOpen.Error!));
            }

            oldHandle = oldOpen.Value;
            Result<ValidatedRecord> oldValidation = await ValidateRecordAsync(
                context,
                oldHandle,
                canonicalLeaf,
                RecoveryRecordFileKind.CanonicalRecord,
                expectedIdentity: null,
                expectedRecord: null,
                cancellationToken);
            if (oldValidation.IsFailure)
            {
                oldHandle.Dispose();
                return Result.Failure(oldValidation.Error!);
            }

            oldRecord = oldValidation.Value;
            if (oldRecord.Record.RecordId != record.RecordId
                || oldRecord.Record.TaskId != record.TaskId)
            {
                oldHandle.Dispose();
                return Result.Failure(Error(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_MISMATCH));
            }
        }

        try
        {
            Result<PreparedTemporary> preparedResult = await PrepareTemporaryAsync(
                context,
                record,
                cancellationToken);
            if (preparedResult.IsFailure)
            {
                return Result.Failure(preparedResult.Error!);
            }

            using PreparedTemporary prepared = preparedResult.Value;
            try
            {
                if (oldHandle is not null && oldRecord is not null)
                {
                    Result<ValidatedRecord> oldFinal = await ValidateRecordAsync(
                        context,
                        oldHandle,
                        canonicalLeaf,
                        RecoveryRecordFileKind.CanonicalRecord,
                        oldRecord.Identity,
                        oldRecord.Record,
                        cancellationToken);
                    if (oldFinal.IsFailure)
                    {
                        return await FailAndCleanupTemporaryAsync(
                            context,
                            prepared,
                            oldFinal.Error!);
                    }

                    Result oldMapping = VerifyLeafMapping(
                        context,
                        canonicalLeaf,
                        oldRecord.Identity);
                    if (oldMapping.IsFailure)
                    {
                        return await FailAndCleanupTemporaryAsync(
                            context,
                            prepared,
                            oldMapping.Error!);
                    }
                }

                Result rename = _filePlatform.Rename(
                    prepared.Handle,
                    context.DirectoryHandle,
                    canonicalLeaf,
                    replaceExisting: isUpdate);
                if (rename.IsFailure)
                {
                    return await FailAndCleanupTemporaryAsync(
                        context,
                        prepared,
                        Normalize(rename.Error!));
                }

                prepared.Committed = true;
                try
                {
                    _testHook?.OnCommitPoint(
                        RecoveryRecordCommitPoint.AfterAtomicCommit,
                        Path.Combine(_recordsDirectory, prepared.LeafName),
                        GetRecordPath(record.RecordId),
                        string.Empty);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    return PostCommitFailure();
                }

                Result<ValidatedRecord> postCommit = await ValidateRecordAsync(
                    context,
                    prepared.Handle,
                    canonicalLeaf,
                    RecoveryRecordFileKind.CanonicalRecord,
                    prepared.Identity!,
                    record,
                    CancellationToken.None);
                if (postCommit.IsFailure
                    || VerifyLeafMapping(context, canonicalLeaf, prepared.Identity!).IsFailure
                    || VerifyDirectoryIdentity(context).IsFailure)
                {
                    return PostCommitFailure();
                }

                try
                {
                    _testHook?.OnCommitPoint(
                        RecoveryRecordCommitPoint.AfterFinalVerification,
                        GetRecordPath(record.RecordId),
                        GetRecordPath(record.RecordId),
                        string.Empty);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    return PostCommitFailure();
                }

                return Result.Success();
            }
            catch (Exception) when (prepared.Committed)
            {
                return PostCommitFailure();
            }
            catch (OperationCanceledException) when (!prepared.Committed)
            {
                Result cleanup = ProveTemporaryCleanup(context, prepared);
                if (cleanup.IsFailure)
                {
                    return cleanup;
                }

                throw;
            }
            catch (Exception) when (!prepared.Committed)
            {
                Result cleanup = ProveTemporaryCleanup(context, prepared);
                if (cleanup.IsFailure)
                {
                    return cleanup;
                }

                throw;
            }
        }
        finally
        {
            oldHandle?.Dispose();
        }
    }

    private async ValueTask<Result<PreparedTemporary>> PrepareTemporaryAsync(
        StoreContext context,
        RecoveryRecord record,
        CancellationToken cancellationToken)
    {
        string leaf = $"{record.RecordId:D}.tmp-{Guid.NewGuid():D}";
        Result<SafeFileHandle> create = _filePlatform.CreateTemporary(
            context.DirectoryHandle,
            leaf);
        if (create.IsFailure)
        {
            return Result<PreparedTemporary>.Failure(Normalize(create.Error!));
        }

        var prepared = new PreparedTemporary(create.Value, leaf);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result<RecoveryRecordFileIdentity> initialIdentity = ValidateBasicHandle(
                context,
                prepared.Handle,
                leaf,
                expectedIdentity: null);
            if (initialIdentity.IsFailure)
            {
                return await FailPreparedTemporaryAsync(
                    context,
                    prepared,
                    initialIdentity.Error!);
            }

            Result<RecoveryRecordFileSecuritySnapshot> security =
                await _fileSecurity.ApplyAndVerifyAsync(
                    prepared.Handle,
                    RecoveryRecordFileKind.TemporaryRecord,
                    cancellationToken);
            if (security.IsFailure)
            {
                if (security.Error!.Code
                    == BrokerErrorCodes.FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED)
                {
                    _writeSafetyState.BlockWrites(security.Error.Code);
                }

                return await FailPreparedTemporaryAsync(
                    context,
                    prepared,
                    Normalize(security.Error!));
            }

            Result<RecoveryRecordFileIdentity> securedIdentity = ValidateBasicHandle(
                context,
                prepared.Handle,
                leaf,
                initialIdentity.Value);
            if (securedIdentity.IsFailure
                || securedIdentity.Value != security.Value.Identity)
            {
                return await FailPreparedTemporaryAsync(
                    context,
                    prepared,
                    Error(BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH));
            }

            byte[] bytes;
            try
            {
                bytes = _container.Serialize(record);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ArgumentException or CryptographicException)
            {
                return await FailPreparedTemporaryAsync(
                    context,
                    prepared,
                    Error(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_WRITE_FAILED));
            }

            Result write = _filePlatform.WriteAll(prepared.Handle, bytes);
            Result flush = write.IsSuccess
                ? _filePlatform.Flush(prepared.Handle)
                : write;
            if (flush.IsFailure)
            {
                return await FailPreparedTemporaryAsync(
                    context,
                    prepared,
                    Normalize(flush.Error!));
            }

            try
            {
                _testHook?.OnCommitPoint(
                    RecoveryRecordCommitPoint.AfterTemporaryFlush,
                    Path.Combine(_recordsDirectory, leaf),
                    GetRecordPath(record.RecordId),
                    string.Empty);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                return await FailPreparedTemporaryAsync(
                    context,
                    prepared,
                    Error(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_WRITE_FAILED));
            }

            Result<ValidatedRecord> validation = await ValidateRecordAsync(
                context,
                prepared.Handle,
                leaf,
                RecoveryRecordFileKind.TemporaryRecord,
                securedIdentity.Value,
                record,
                cancellationToken);
            if (validation.IsFailure)
            {
                return await FailPreparedTemporaryAsync(
                    context,
                    prepared,
                    validation.Error!);
            }

            prepared.Identity = validation.Value.Identity;
            try
            {
                _testHook?.OnCommitPoint(
                    RecoveryRecordCommitPoint.AfterTemporaryVerification,
                    Path.Combine(_recordsDirectory, leaf),
                    GetRecordPath(record.RecordId),
                    string.Empty);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                return await FailPreparedTemporaryAsync(
                    context,
                    prepared,
                    Error(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_WRITE_FAILED));
            }

            return Result<PreparedTemporary>.Success(prepared);
        }
        catch (OperationCanceledException)
        {
            Result cleanup = ProveTemporaryCleanup(context, prepared);
            if (cleanup.IsFailure)
            {
                return Result<PreparedTemporary>.Failure(cleanup.Error!);
            }

            throw;
        }
        catch (Exception)
        {
            Result cleanup = ProveTemporaryCleanup(context, prepared);
            if (cleanup.IsFailure)
            {
                return Result<PreparedTemporary>.Failure(cleanup.Error!);
            }

            throw;
        }
    }

    private async ValueTask<Result<RecoveryRecord>> ReadByLeafAsync(
        StoreContext context,
        Guid recordId,
        RecoveryRecordFileIdentity? expectedIdentity,
        RecoveryRecord? expectedRecord,
        CancellationToken cancellationToken)
    {
        string leaf = CanonicalLeaf(recordId);
        Result<SafeFileHandle> open = _filePlatform.OpenExisting(context.DirectoryHandle, leaf);
        if (open.IsFailure)
        {
            return Result<RecoveryRecord>.Failure(Normalize(open.Error!));
        }

        using SafeFileHandle handle = open.Value;
        Result<ValidatedRecord> validation = await ValidateRecordAsync(
            context,
            handle,
            leaf,
            RecoveryRecordFileKind.CanonicalRecord,
            expectedIdentity,
            expectedRecord,
            cancellationToken);
        if (validation.IsFailure)
        {
            return Result<RecoveryRecord>.Failure(validation.Error!);
        }

        return validation.Value.Record.RecordId == recordId
            ? Result<RecoveryRecord>.Success(validation.Value.Record)
            : Result<RecoveryRecord>.Failure(Error(
                BrokerErrorCodes.FSL_E_RECOVERY_RECORD_ID_MISMATCH));
    }

    private async ValueTask<Result<ValidatedRecord>> ValidateRecordAsync(
        StoreContext context,
        SafeFileHandle handle,
        string leaf,
        RecoveryRecordFileKind fileKind,
        RecoveryRecordFileIdentity? expectedIdentity,
        RecoveryRecord? expectedRecord,
        CancellationToken cancellationToken)
    {
        Result<RecoveryRecordFileIdentity> basic = ValidateBasicHandle(
            context,
            handle,
            leaf,
            expectedIdentity);
        if (basic.IsFailure)
        {
            return Result<ValidatedRecord>.Failure(basic.Error!);
        }

        Result<RecoveryRecordFileSecuritySnapshot> security = await _fileSecurity.VerifyAsync(
            handle,
            fileKind,
            cancellationToken);
        if (security.IsFailure)
        {
            return Result<ValidatedRecord>.Failure(Normalize(security.Error!));
        }

        if (security.Value.Identity != basic.Value)
        {
            return Result<ValidatedRecord>.Failure(Error(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH));
        }

        Result<byte[]> bytes = _filePlatform.ReadAll(
            handle,
            RecoveryRecordContainer.HeaderLength
                + RecoveryRecordContainer.MaximumProtectedPayloadLength);
        if (bytes.IsFailure)
        {
            return Result<ValidatedRecord>.Failure(Normalize(bytes.Error!));
        }

        RecoveryRecordReadResult read = _container.Deserialize(bytes.Value);
        if (!read.IsSuccess)
        {
            return Result<ValidatedRecord>.Failure(Error(read.Error!.Code));
        }

        if (expectedRecord is not null && read.Record != expectedRecord)
        {
            return Result<ValidatedRecord>.Failure(Error(
                BrokerErrorCodes.FSL_E_RECOVERY_RECORD_MISMATCH));
        }

        Result<RecoveryRecordFileIdentity> afterRead = ValidateBasicHandle(
            context,
            handle,
            leaf,
            basic.Value);
        return afterRead.IsSuccess
            ? Result<ValidatedRecord>.Success(new(read.Record!, afterRead.Value))
            : Result<ValidatedRecord>.Failure(afterRead.Error!);
    }

    private Result<RecoveryRecordFileIdentity> ValidateBasicHandle(
        StoreContext context,
        SafeFileHandle handle,
        string leaf,
        RecoveryRecordFileIdentity? expectedIdentity)
    {
        Result<NativeMethods.FileAttributeTagInfo> attributes = _filePlatform.GetAttributes(handle);
        if (attributes.IsFailure
            || (attributes.Value.FileAttributes
                & (NativeMethods.FileAttributeDirectory | NativeMethods.FileAttributeReparsePoint)) != 0)
        {
            return Result<RecoveryRecordFileIdentity>.Failure(Error(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH));
        }

        Result<RecoveryRecordFileIdentity> identity = _filePlatform.GetIdentity(handle);
        if (identity.IsFailure)
        {
            return Result<RecoveryRecordFileIdentity>.Failure(Normalize(identity.Error!));
        }

        if (identity.Value.NumberOfLinks != 1
            || identity.Value.VolumeSerialNumber != context.DirectoryIdentity.VolumeSerialNumber
            || (expectedIdentity is not null && identity.Value != expectedIdentity))
        {
            return Result<RecoveryRecordFileIdentity>.Failure(Error(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH));
        }

        Result<string> finalPath = _filePlatform.GetFinalPath(handle);
        if (finalPath.IsFailure
            || !string.Equals(
                Path.Combine(_recordsDirectory, leaf),
                finalPath.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<RecoveryRecordFileIdentity>.Failure(Error(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH));
        }

        return identity;
    }

    private async ValueTask<Result<StoreContext>> OpenContextAsync(
        bool writing,
        CancellationToken cancellationToken)
    {
        if (writing && _writeSafetyState.IsWriteBlocked)
        {
            return Result<StoreContext>.Failure(Error(
                _writeSafetyState.BlockingErrorCode
                    ?? BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED));
        }

        if (_createDirectory)
        {
            Directory.CreateDirectory(_recordsDirectory);
        }

        RecoveryStoreMutexLease lease = await _mutex.AcquireAsync(cancellationToken);
        Result<SafeFileHandle> open = _filePlatform.OpenDirectory(_recordsDirectory);
        if (open.IsFailure)
        {
            lease.Dispose();
            return Result<StoreContext>.Failure(Normalize(open.Error!));
        }

        SafeFileHandle directoryHandle = open.Value;
        ProtectedPathSecurityCheckResult protectedPath = await _protectedPathVerifier.VerifyAsync(
            _protectedPathRequest,
            cancellationToken);
        if (!protectedPath.IsTrusted || protectedPath.ErrorCode is not null)
        {
            directoryHandle.Dispose();
            lease.Dispose();
            return Result<StoreContext>.Failure(Error(
                protectedPath.ErrorCode
                    ?? BrokerErrorCodes.FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED));
        }

        Result<RecoveryRecordFileIdentity> identity = _filePlatform.GetIdentity(directoryHandle);
        if (identity.IsFailure)
        {
            directoryHandle.Dispose();
            lease.Dispose();
            return Result<StoreContext>.Failure(Normalize(identity.Error!));
        }

        return Result<StoreContext>.Success(new(lease, directoryHandle, identity.Value));
    }

    private Result VerifyLeafMapping(
        StoreContext context,
        string leaf,
        RecoveryRecordFileIdentity expectedIdentity)
    {
        Result<RecoveryRecordFileIdentity?> mapping = _filePlatform.GetLeafIdentity(
            context.DirectoryHandle,
            leaf);
        return mapping.IsSuccess && mapping.Value == expectedIdentity
            ? Result.Success()
            : Result.Failure(Error(BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH));
    }

    private Result VerifyDirectoryIdentity(StoreContext context)
    {
        Result<RecoveryRecordFileIdentity> current = _filePlatform.GetIdentity(
            context.DirectoryHandle);
        return current.IsSuccess && current.Value == context.DirectoryIdentity
            ? Result.Success()
            : Result.Failure(Error(BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_CHANGED));
    }

    private async ValueTask<Result<PreparedTemporary>> FailPreparedTemporaryAsync(
        StoreContext context,
        PreparedTemporary prepared,
        Error error)
    {
        Result cleanup = ProveTemporaryCleanup(context, prepared);
        await Task.CompletedTask;
        if (cleanup.IsFailure)
        {
            return Result<PreparedTemporary>.Failure(cleanup.Error!);
        }

        return Result<PreparedTemporary>.Failure(Normalize(error));
    }

    private async ValueTask<Result> FailAndCleanupTemporaryAsync(
        StoreContext context,
        PreparedTemporary prepared,
        Error error)
    {
        if (prepared.Committed)
        {
            return PostCommitFailure();
        }

        Result cleanup = ProveTemporaryCleanup(context, prepared);
        await Task.CompletedTask;
        if (cleanup.IsFailure)
        {
            return cleanup;
        }

        return Result.Failure(Normalize(error));
    }

    private Result ProveTemporaryCleanup(
        StoreContext context,
        PreparedTemporary prepared)
    {
        SafeFileHandle handle = prepared.DetachHandle();
        bool cleanupProven = true;
        Result delete;
        try
        {
            delete = _filePlatform.Delete(handle);
        }
        catch (Exception)
        {
            delete = Result.Failure(Error(
                BrokerErrorCodes.FSL_E_RECOVERY_TEMP_CLEANUP_FAILED));
        }

        if (delete.IsFailure)
        {
            cleanupProven = false;
        }

        try
        {
            Result close = _filePlatform.CloseAfterDisposition(handle);
            if (close.IsFailure)
            {
                cleanupProven = false;
            }
        }
        catch (Exception)
        {
            cleanupProven = false;
            handle.Dispose();
        }

        try
        {
            Result<RecoveryRecordFileIdentity?> remaining = _filePlatform.GetLeafIdentity(
                context.DirectoryHandle,
                prepared.LeafName);
            if (remaining.IsFailure || remaining.Value is not null)
            {
                cleanupProven = false;
            }
        }
        catch (Exception)
        {
            cleanupProven = false;
        }

        try
        {
            if (VerifyDirectoryIdentity(context).IsFailure)
            {
                cleanupProven = false;
            }
        }
        catch (Exception)
        {
            cleanupProven = false;
        }

        if (cleanupProven)
        {
            return Result.Success();
        }

        _writeSafetyState.BlockWrites(BrokerErrorCodes.FSL_E_RECOVERY_TEMP_CLEANUP_FAILED);
        return Result.Failure(Error(BrokerErrorCodes.FSL_E_RECOVERY_TEMP_CLEANUP_FAILED));
    }

    private static Result PostCommitFailure() => Result.Failure(Error(
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED));

    private static string CanonicalLeaf(Guid recordId) => $"{recordId:D}.fslr";

    private static Error Normalize(Error error)
    {
        try
        {
            return RecoveryRecordFileErrorFactory.Create(error.Code);
        }
        catch (ArgumentOutOfRangeException)
        {
            return error;
        }
    }

    private static Error Error(string code)
    {
        try
        {
            return RecoveryRecordFileErrorFactory.Create(code);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new Error(code, code, ErrorCategory.UnrecoverableError);
        }
    }

    private sealed class StoreContext(
        RecoveryStoreMutexLease lease,
        SafeFileHandle directoryHandle,
        RecoveryRecordFileIdentity directoryIdentity) : IDisposable
    {
        internal SafeFileHandle DirectoryHandle { get; } = directoryHandle;
        internal RecoveryRecordFileIdentity DirectoryIdentity { get; } = directoryIdentity;

        public void Dispose()
        {
            DirectoryHandle.Dispose();
            lease.Dispose();
        }
    }

    private sealed class PreparedTemporary(
        SafeFileHandle handle,
        string leafName) : IDisposable
    {
        private SafeFileHandle? _handle = handle;

        internal SafeFileHandle Handle => _handle
            ?? throw new ObjectDisposedException(nameof(PreparedTemporary));
        internal string LeafName { get; } = leafName;
        internal RecoveryRecordFileIdentity? Identity { get; set; }
        internal bool Committed { get; set; }

        internal SafeFileHandle DetachHandle() => Interlocked.Exchange(ref _handle, null)
            ?? throw new ObjectDisposedException(nameof(PreparedTemporary));

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    private sealed record ValidatedRecord(
        RecoveryRecord Record,
        RecoveryRecordFileIdentity Identity);
}

internal enum RecoveryRecordCommitPoint
{
    AfterTemporaryFlush,
    AfterTemporaryVerification,
    AfterAtomicCommit,
    AfterFinalVerification,
}

internal interface IFileRecoveryRecordStoreTestHook
{
    void OnCommitPoint(
        RecoveryRecordCommitPoint point,
        string temporaryPath,
        string finalPath,
        string backupPath);
}
