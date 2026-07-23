using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery;

internal sealed class WindowsRecoveryReadinessStore : IRecoveryReadinessStore
{
    internal const string CanonicalLeafName = RecoveryReadinessPolicy.CanonicalLeafName;
    internal const string TemporaryPrefix = "recovery-readiness.v1.tmp-";
    private readonly IRecoveryReadinessFilePlatform _files;
    private readonly IRecoveryReadinessFileSecurity _security;
    private readonly IRecoveryReadinessMutex _mutex;
    private readonly IClock _clock;

    internal WindowsRecoveryReadinessStore(
        IRecoveryReadinessFilePlatform files,
        IRecoveryReadinessFileSecurity security,
        IRecoveryReadinessMutex mutex,
        IClock clock)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _security = security ?? throw new ArgumentNullException(nameof(security));
        _mutex = mutex ?? throw new ArgumentNullException(nameof(mutex));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal static WindowsRecoveryReadinessStore CreateProduction(IClock clock) => new(
        new WindowsRecoveryReadinessFilePlatform(),
        new RecoveryReadinessFileSecurity(),
        RecoveryReadinessMutex.CreateProduction(),
        clock);

    public async ValueTask PublishAsync(
        RecoveryReadinessSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using RecoveryStoreMutexLease lease = await _mutex.AcquireAsync(cancellationToken);
        try
        {
            using StoreDirectory directory = await OpenDirectoryAsync(cancellationToken);
            RecoveryReadinessSnapshot? previous = await TryReadCurrentAsync(
                directory,
                validateFreshness: false,
                cancellationToken);
            string? validation = RecoveryReadinessPolicy.Validate(
                snapshot,
                _clock.UtcNow,
                previous);
            if (validation is not null
                || (previous is null && snapshot.Sequence != 1)
                || (previous is not null
                    && previous.ServiceInstanceId == snapshot.ServiceInstanceId
                    && snapshot.Sequence != previous.Sequence + 1)
                || (previous is not null
                    && previous.ServiceInstanceId != snapshot.ServiceInstanceId
                    && snapshot.Sequence != 1))
            {
                throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID);
            }

            byte[] bytes = RecoveryReadinessJson.Serialize(snapshot);
            string tempLeaf = TemporaryPrefix + Guid.NewGuid().ToString("D");
            Result<SafeFileHandle> create = _files.CreateTemporary(directory.Handle, tempLeaf);
            if (create.IsFailure)
            {
                throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_PUBLISH_FAILED);
            }

            SafeFileHandle temp = create.Value;
            bool committed = false;
            try
            {
                RecoveryRecordFileIdentity tempIdentity = await ValidateNewTempAsync(
                    directory,
                    temp,
                    tempLeaf,
                    cancellationToken);
                RequireSuccess(_files.WriteAll(temp, bytes));
                RequireSuccess(_files.Flush(temp));
                Result<byte[]> readback = _files.ReadAll(temp, RecoveryReadinessPolicy.MaximumLength);
                if (readback.IsFailure
                    || !readback.Value.AsSpan().SequenceEqual(bytes)
                    || RecoveryReadinessJson.Deserialize(readback.Value).IsFailure)
                {
                    throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_PUBLISH_FAILED);
                }

                await VerifyIdentityAndSecurityAsync(
                    temp,
                    tempIdentity,
                    RecoveryReadinessObjectKind.TemporaryFile,
                    cancellationToken);
                RequireSuccess(_files.Rename(temp, directory.Handle, CanonicalLeafName));
                committed = true;
                await VerifyCommittedAsync(
                    directory,
                    temp,
                    tempIdentity,
                    snapshot,
                    cancellationToken);
            }
            finally
            {
                if (!committed && !temp.IsClosed)
                {
                    Result disposition = _files.Delete(temp);
                    Result close = _files.CloseAfterDisposition(temp);
                    if (disposition.IsFailure || close.IsFailure)
                    {
                        throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_PUBLISH_FAILED);
                    }
                }

                temp.Dispose();
            }
        }
        catch (RecoveryReadinessException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_PUBLISH_FAILED, exception);
        }
    }

    public async ValueTask<RecoveryReadinessSnapshot> ReadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using StoreDirectory directory = await OpenDirectoryAsync(cancellationToken);
            RecoveryReadinessSnapshot? snapshot = await TryReadCurrentAsync(
                directory,
                validateFreshness: true,
                cancellationToken);
            return snapshot ?? throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_NOT_FOUND);
        }
        catch (RecoveryReadinessException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_OPEN_FAILED, exception);
        }
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        using RecoveryStoreMutexLease lease = await _mutex.AcquireAsync(cancellationToken);
        try
        {
            using StoreDirectory directory = await OpenDirectoryAsync(cancellationToken);
            Result<RecoveryRecordFileIdentity?> leaf = _files.GetLeafIdentity(
                directory.Handle,
                CanonicalLeafName);
            if (leaf.IsFailure)
            {
                throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_DELETE_FAILED);
            }

            if (leaf.Value is null)
            {
                return;
            }

            Result<SafeFileHandle> open = _files.OpenExisting(directory.Handle, CanonicalLeafName);
            if (open.IsFailure)
            {
                throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_DELETE_FAILED);
            }

            SafeFileHandle canonical = open.Value;
            try
            {
                RecoveryRecordFileIdentity identity = await VerifyFileAsync(
                    directory,
                    canonical,
                    CanonicalLeafName,
                    RecoveryReadinessObjectKind.CanonicalFile,
                    cancellationToken);
                if (identity != leaf.Value)
                {
                    throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED);
                }

                RequireSuccess(_files.Delete(canonical));
                RequireSuccess(_files.CloseAfterDisposition(canonical));
                Result<RecoveryRecordFileIdentity?> after = _files.GetLeafIdentity(
                    directory.Handle,
                    CanonicalLeafName);
                if (after.IsFailure || after.Value is not null)
                {
                    throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_DELETE_FAILED);
                }

                await VerifyDirectoryIdentityAsync(directory, cancellationToken);
            }
            finally
            {
                canonical.Dispose();
            }
        }
        catch (RecoveryReadinessException exception)
        {
            if (exception.Code == BrokerErrorCodes.FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED)
            {
                throw;
            }

            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_DELETE_FAILED, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_DELETE_FAILED, exception);
        }
    }

    private async ValueTask<StoreDirectory> OpenDirectoryAsync(
        CancellationToken cancellationToken)
    {
        Result<SafeFileHandle> open = _files.OpenDirectory();
        if (open.IsFailure)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_OPEN_FAILED);
        }

        SafeFileHandle handle = open.Value;
        try
        {
            Result<NativeMethods.FileAttributeTagInfo> attributes = _files.GetAttributes(handle);
            Result<string> finalPath = _files.GetFinalPath(handle);
            Result<RecoveryRecordFileIdentity> security = await _security.VerifyAsync(
                handle,
                RecoveryReadinessObjectKind.Directory,
                cancellationToken);
            if (attributes.IsFailure
                || (attributes.Value.FileAttributes & NativeMethods.FileAttributeDirectory) == 0
                || (attributes.Value.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0
                || finalPath.IsFailure
                || !string.Equals(
                    Path.TrimEndingDirectorySeparator(finalPath.Value),
                    Path.TrimEndingDirectorySeparator(_files.ReadinessDirectory),
                    StringComparison.OrdinalIgnoreCase)
                || security.IsFailure)
            {
                throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID);
            }

            return new StoreDirectory(handle, security.Value);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private async ValueTask<RecoveryReadinessSnapshot?> TryReadCurrentAsync(
        StoreDirectory directory,
        bool validateFreshness,
        CancellationToken cancellationToken)
    {
        Result<RecoveryRecordFileIdentity?> leaf = _files.GetLeafIdentity(
            directory.Handle,
            CanonicalLeafName);
        if (leaf.IsFailure)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_OPEN_FAILED);
        }

        if (leaf.Value is null)
        {
            return null;
        }

        Result<SafeFileHandle> open = _files.OpenExisting(directory.Handle, CanonicalLeafName);
        if (open.IsFailure)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_OPEN_FAILED);
        }

        using SafeFileHandle canonical = open.Value;
        RecoveryRecordFileIdentity identity = await VerifyFileAsync(
            directory,
            canonical,
            CanonicalLeafName,
            RecoveryReadinessObjectKind.CanonicalFile,
            cancellationToken);
        if (identity != leaf.Value)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED);
        }

        Result<byte[]> bytes = _files.ReadAll(canonical, RecoveryReadinessPolicy.MaximumLength);
        if (bytes.IsFailure || bytes.Value.Length == 0)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID);
        }

        Result<RecoveryReadinessSnapshot> parsed = RecoveryReadinessJson.Deserialize(bytes.Value);
        if (parsed.IsFailure)
        {
            throw Error(parsed.Error!.Code);
        }

        if (validateFreshness)
        {
            string? validation = RecoveryReadinessPolicy.Validate(parsed.Value, _clock.UtcNow);
            if (validation is not null)
            {
                throw Error(validation);
            }
        }

        await VerifyIdentityAndSecurityAsync(
            canonical,
            identity,
            RecoveryReadinessObjectKind.CanonicalFile,
            cancellationToken);
        await VerifyDirectoryIdentityAsync(directory, cancellationToken);
        return parsed.Value;
    }

    private async ValueTask<RecoveryRecordFileIdentity> ValidateNewTempAsync(
        StoreDirectory directory,
        SafeFileHandle temp,
        string tempLeaf,
        CancellationToken cancellationToken)
    {
        Result<NativeMethods.FileAttributeTagInfo> attributes = _files.GetAttributes(temp);
        Result<string> finalPath = _files.GetFinalPath(temp);
        if (attributes.IsFailure
            || (attributes.Value.FileAttributes
                & (NativeMethods.FileAttributeDirectory | NativeMethods.FileAttributeReparsePoint)) != 0
            || finalPath.IsFailure
            || !string.Equals(
                finalPath.Value,
                Path.Combine(_files.ReadinessDirectory, tempLeaf),
                StringComparison.OrdinalIgnoreCase))
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED);
        }

        Result<RecoveryRecordFileIdentity> secured = await _security.ApplyAndVerifyAsync(
            temp,
            RecoveryReadinessObjectKind.TemporaryFile,
            cancellationToken);
        if (secured.IsFailure || secured.Value.NumberOfLinks != 1)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID);
        }

        if (secured.Value.VolumeSerialNumber != directory.Identity.VolumeSerialNumber)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED);
        }

        return secured.Value;
    }

    private async ValueTask<RecoveryRecordFileIdentity> VerifyFileAsync(
        StoreDirectory directory,
        SafeFileHandle handle,
        string expectedLeaf,
        RecoveryReadinessObjectKind kind,
        CancellationToken cancellationToken)
    {
        Result<NativeMethods.FileAttributeTagInfo> attributes = _files.GetAttributes(handle);
        Result<string> finalPath = _files.GetFinalPath(handle);
        Result<RecoveryRecordFileIdentity> security = await _security.VerifyAsync(
            handle,
            kind,
            cancellationToken);
        if (attributes.IsFailure
            || (attributes.Value.FileAttributes
                & (NativeMethods.FileAttributeDirectory | NativeMethods.FileAttributeReparsePoint)) != 0
            || finalPath.IsFailure
            || !string.Equals(
                finalPath.Value,
                Path.Combine(_files.ReadinessDirectory, expectedLeaf),
                StringComparison.OrdinalIgnoreCase)
            || security.IsFailure
            || security.Value.NumberOfLinks != 1
            || security.Value.VolumeSerialNumber != directory.Identity.VolumeSerialNumber)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID);
        }

        return security.Value;
    }

    private async ValueTask VerifyCommittedAsync(
        StoreDirectory directory,
        SafeFileHandle canonical,
        RecoveryRecordFileIdentity expectedIdentity,
        RecoveryReadinessSnapshot expectedSnapshot,
        CancellationToken cancellationToken)
    {
        RecoveryRecordFileIdentity actual = await VerifyFileAsync(
            directory,
            canonical,
            CanonicalLeafName,
            RecoveryReadinessObjectKind.CanonicalFile,
            cancellationToken);
        Result<byte[]> bytes = _files.ReadAll(canonical, RecoveryReadinessPolicy.MaximumLength);
        Result<RecoveryReadinessSnapshot> parsed = bytes.IsSuccess
            ? RecoveryReadinessJson.Deserialize(bytes.Value)
            : Result<RecoveryReadinessSnapshot>.Failure(bytes.Error!);
        Result<RecoveryRecordFileIdentity?> leaf = _files.GetLeafIdentity(
            directory.Handle,
            CanonicalLeafName);
        if (actual != expectedIdentity
            || parsed.IsFailure
            || parsed.Value != expectedSnapshot
            || leaf.IsFailure
            || leaf.Value != expectedIdentity)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED);
        }

        await VerifyDirectoryIdentityAsync(directory, cancellationToken);
    }

    private async ValueTask VerifyIdentityAndSecurityAsync(
        SafeFileHandle handle,
        RecoveryRecordFileIdentity expected,
        RecoveryReadinessObjectKind kind,
        CancellationToken cancellationToken)
    {
        Result<RecoveryRecordFileIdentity> actual = await _security.VerifyAsync(
            handle,
            kind,
            cancellationToken);
        if (actual.IsFailure || actual.Value != expected)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED);
        }
    }

    private async ValueTask VerifyDirectoryIdentityAsync(
        StoreDirectory directory,
        CancellationToken cancellationToken)
    {
        Result<RecoveryRecordFileIdentity> current = await _security.VerifyAsync(
            directory.Handle,
            RecoveryReadinessObjectKind.Directory,
            cancellationToken);
        if (current.IsFailure || current.Value != directory.Identity)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED);
        }
    }

    private static void RequireSuccess(Result result)
    {
        if (result.IsFailure)
        {
            throw Error(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_PUBLISH_FAILED);
        }
    }

    private static RecoveryReadinessException Error(string code) => new(code);

    private static RecoveryReadinessException Error(string code, Exception innerException) =>
        new(code, innerException);

    private sealed class StoreDirectory(
        SafeFileHandle handle,
        RecoveryRecordFileIdentity identity) : IDisposable
    {
        internal SafeFileHandle Handle { get; } = handle;
        internal RecoveryRecordFileIdentity Identity { get; } = identity;
        public void Dispose() => Handle.Dispose();
    }
}
