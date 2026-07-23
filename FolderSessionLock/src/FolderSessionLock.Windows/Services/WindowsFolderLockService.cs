using System.Security.Principal;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Windows.Services;

public sealed class WindowsFolderLockService : IFolderLockService
{
    private static readonly Error DuplicateTaskError = new(
        "windows.lock.task_id_conflict",
        "The task ID was previously used for a different lock lifecycle.",
        ErrorCategory.ValidationFailed);

    private static readonly Error PathConflictError = new(
        "windows.lock.path_conflict",
        "The directory conflicts with an active folder lock.",
        ErrorCategory.ValidationFailed);

    private static readonly Error UnknownTaskError = new(
        "windows.lock.task_not_found",
        "The folder lock task is not active.",
        ErrorCategory.ValidationFailed);

    private static readonly Error UnknownAclStateError = new(
        "windows.lock.acl_state_unknown",
        "The folder lock ACL state cannot be safely removed.",
        ErrorCategory.UnrecoverableError);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISessionIdentityProvider _sessionIdentityProvider;
    private readonly WindowsFolderPathValidator _pathValidator;
    private readonly IFolderPathRelationService _pathRelationService;
    private readonly DirectoryAclEditor _aclEditor;
    private readonly IFolderLockRecoveryTransaction _recoveryTransaction;
    private readonly IWindowsFolderLockServiceTestHook? _testHook;
    private readonly Dictionary<Guid, ActiveFolderLockRecord> _active = new();
    private readonly HashSet<Guid> _removed = [];

    public WindowsFolderLockService(
        ISessionIdentityProvider sessionIdentityProvider,
        WindowsFolderPathValidator pathValidator,
        IFolderPathRelationService pathRelationService,
        DirectoryAclEditor aclEditor,
        IFolderLockRecoveryTransaction recoveryTransaction)
    {
        _sessionIdentityProvider = sessionIdentityProvider
            ?? throw new ArgumentNullException(nameof(sessionIdentityProvider));
        _pathValidator = pathValidator ?? throw new ArgumentNullException(nameof(pathValidator));
        _pathRelationService = pathRelationService
            ?? throw new ArgumentNullException(nameof(pathRelationService));
        _aclEditor = aclEditor ?? throw new ArgumentNullException(nameof(aclEditor));
        _recoveryTransaction = recoveryTransaction
            ?? throw new ArgumentNullException(nameof(recoveryTransaction));
    }

    internal WindowsFolderLockService(
        ISessionIdentityProvider sessionIdentityProvider,
        WindowsFolderPathValidator pathValidator,
        IFolderPathRelationService pathRelationService,
        DirectoryAclEditor aclEditor)
        : this(
            sessionIdentityProvider,
            pathValidator,
            pathRelationService,
            aclEditor,
            InMemoryFolderLockRecoveryTransaction.Instance)
    {
    }

    internal WindowsFolderLockService(
        ISessionIdentityProvider sessionIdentityProvider,
        WindowsFolderPathValidator pathValidator,
        IFolderPathRelationService pathRelationService,
        DirectoryAclEditor aclEditor,
        IWindowsFolderLockServiceTestHook testHook)
        : this(
            sessionIdentityProvider,
            pathValidator,
            pathRelationService,
            aclEditor,
            InMemoryFolderLockRecoveryTransaction.Instance,
            testHook)
    {
    }

    internal WindowsFolderLockService(
        ISessionIdentityProvider sessionIdentityProvider,
        WindowsFolderPathValidator pathValidator,
        IFolderPathRelationService pathRelationService,
        DirectoryAclEditor aclEditor,
        IFolderLockRecoveryTransaction recoveryTransaction,
        IWindowsFolderLockServiceTestHook testHook)
        : this(
            sessionIdentityProvider,
            pathValidator,
            pathRelationService,
            aclEditor,
            recoveryTransaction)
    {
        _testHook = testHook ?? throw new ArgumentNullException(nameof(testHook));
    }

    public async ValueTask<Result<Guid>> CreateLockAsync(
        FolderLockRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Result<SessionIdentity> identityResult =
            await _sessionIdentityProvider.GetCurrentAsync(cancellationToken);
        if (identityResult.IsFailure)
        {
            return Result<Guid>.Failure(identityResult.Error!);
        }

        Result<FolderPath> folderPathResult = FolderPath.Create(request.FolderPath);
        if (folderPathResult.IsFailure)
        {
            return Result<Guid>.Failure(folderPathResult.Error!);
        }

        SecurityIdentifier logonSid;
        try
        {
            logonSid = new SecurityIdentifier(identityResult.Value.LogonSid);
        }
        catch (ArgumentException)
        {
            return Result<Guid>.Failure(UnknownAclStateError);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active.TryGetValue(request.TaskId, out ActiveFolderLockRecord? existingRecord))
            {
                return existingRecord.FolderPath == folderPathResult.Value
                    && existingRecord.Duration == request.Duration
                    && existingRecord.SessionIdentity == identityResult.Value
                        ? Result<Guid>.Success(request.TaskId)
                        : Result<Guid>.Failure(DuplicateTaskError);
            }

            if (_removed.Contains(request.TaskId))
            {
                return Result<Guid>.Failure(DuplicateTaskError);
            }

            if (HasPathConflict(folderPathResult.Value))
            {
                return Result<Guid>.Failure(PathConflictError);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Result<ValidatedDirectory> validationResult = _pathValidator.Validate(request.FolderPath);
            if (validationResult.IsFailure)
            {
                return Result<Guid>.Failure(validationResult.Error!);
            }

            ValidatedDirectory directory = validationResult.Value;
            if (_active.Values.Any(record => record.Identity == directory.Identity)
                || HasPathConflict(folderPathResult.Value))
            {
                directory.Dispose();
                return Result<Guid>.Failure(PathConflictError);
            }

            _testHook?.BeforeAclWrite();
            Result beforeMappingResult = _pathValidator.VerifyCurrentPathMapping(directory);
            if (beforeMappingResult.IsFailure)
            {
                directory.Dispose();
                return Result<Guid>.Failure(beforeMappingResult.Error!);
            }

            Result<DirectoryAclPreparation> preparationResult =
                _aclEditor.PrepareDenyAce(directory.Handle, logonSid);
            if (preparationResult.IsFailure)
            {
                directory.Dispose();
                return Result<Guid>.Failure(preparationResult.Error!);
            }

            Result<Guid> recoveryResult = await _recoveryTransaction.PrepareAsync(
                request,
                identityResult.Value,
                directory,
                preparationResult.Value.Evidence,
                cancellationToken);
            if (recoveryResult.IsFailure)
            {
                directory.Dispose();
                return Result<Guid>.Failure(recoveryResult.Error!);
            }

            Guid recoveryRecordId = recoveryResult.Value;
            Result<DirectoryAclOperation> addResult = _aclEditor.ApplyPreparedDenyAce(
                directory.Handle,
                preparationResult.Value,
                out DirectoryAclOperation? operation);
            if (addResult.IsFailure)
            {
                if (operation is not null)
                {
                    _active.Add(request.TaskId, new ActiveFolderLockRecord(
                        request.TaskId,
                        folderPathResult.Value,
                        request.Duration,
                        identityResult.Value,
                        directory.Identity,
                        recoveryRecordId,
                        directory,
                        operation));
                }
                else
                {
                    Result deleteResult = await _recoveryTransaction.DeleteAsync(
                        recoveryRecordId,
                        cancellationToken);
                    directory.Dispose();
                    if (deleteResult.IsFailure)
                    {
                        return Result<Guid>.Failure(deleteResult.Error!);
                    }
                }

                return Result<Guid>.Failure(addResult.Error!);
            }

            _testHook?.AfterAclWrite();
            Result afterMappingResult = _pathValidator.VerifyCurrentPathMapping(directory);
            if (afterMappingResult.IsFailure)
            {
                Result rollbackResult = _aclEditor.RemoveDenyAce(
                    directory.Handle,
                    addResult.Value);
                if (rollbackResult.IsFailure)
                {
                    _active.Add(request.TaskId, new ActiveFolderLockRecord(
                        request.TaskId,
                        folderPathResult.Value,
                        request.Duration,
                        identityResult.Value,
                        directory.Identity,
                        recoveryRecordId,
                        directory,
                        addResult.Value));
                    return Result<Guid>.Failure(UnknownAclStateError);
                }

                Result deleteResult = await _recoveryTransaction.DeleteAsync(
                    recoveryRecordId,
                    cancellationToken);
                directory.Dispose();
                return deleteResult.IsSuccess
                    ? Result<Guid>.Failure(afterMappingResult.Error!)
                    : Result<Guid>.Failure(deleteResult.Error!);
            }

            Result appliedResult = await _recoveryTransaction.MarkAppliedAsync(
                recoveryRecordId,
                addResult.Value.Evidence,
                cancellationToken);
            if (appliedResult.IsFailure)
            {
                Result rollbackResult = _aclEditor.RemoveDenyAce(directory.Handle, addResult.Value);
                if (rollbackResult.IsSuccess)
                {
                    Result deleteResult = await _recoveryTransaction.DeleteAsync(
                        recoveryRecordId,
                        cancellationToken);
                    directory.Dispose();
                    if (deleteResult.IsFailure)
                    {
                        return Result<Guid>.Failure(deleteResult.Error!);
                    }
                }
                else
                {
                    _active.Add(request.TaskId, new ActiveFolderLockRecord(
                        request.TaskId,
                        folderPathResult.Value,
                        request.Duration,
                        identityResult.Value,
                        directory.Identity,
                        recoveryRecordId,
                        directory,
                        addResult.Value));
                }

                return Result<Guid>.Failure(appliedResult.Error!);
            }

            _active.Add(request.TaskId, new ActiveFolderLockRecord(
                request.TaskId,
                folderPathResult.Value,
                request.Duration,
                identityResult.Value,
                directory.Identity,
                recoveryRecordId,
                directory,
                addResult.Value));
            return Result<Guid>.Success(request.TaskId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Result> RemoveLockAsync(
        Guid taskId,
        LockRemovalIntent intent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_removed.Contains(taskId))
            {
                return Result.Success();
            }

            if (!_active.TryGetValue(taskId, out ActiveFolderLockRecord? record))
            {
                return Result.Failure(UnknownTaskError);
            }

            if (record.AclOperation is null)
            {
                return Result.Failure(UnknownAclStateError);
            }

            Result pendingResult = await _recoveryTransaction.MarkCleanupPendingAsync(
                record.RecoveryRecordId,
                cancellationToken);
            if (pendingResult.IsFailure)
            {
                return pendingResult;
            }

            Result removeResult = _aclEditor.RemoveDenyAce(
                record.Directory.Handle,
                record.AclOperation);
            if (removeResult.IsFailure)
            {
                _ = await _recoveryTransaction.MarkCleanupFailedAsync(
                    record.RecoveryRecordId,
                    removeResult.Error!,
                    cancellationToken);
                return removeResult;
            }

            Result deleteResult = await _recoveryTransaction.DeleteAsync(
                record.RecoveryRecordId,
                cancellationToken);
            if (deleteResult.IsFailure)
            {
                return deleteResult;
            }

            _active.Remove(taskId);
            _removed.Add(taskId);
            record.Directory.Dispose();
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool HasPathConflict(FolderPath requestedPath) =>
        _active.Values.Any(record =>
            _pathRelationService.GetRelation(record.FolderPath, requestedPath)
                != FolderPathRelation.Unrelated);

    internal ActiveFolderLockRecord? GetActiveRecord(Guid taskId)
    {
        _gate.Wait();
        try
        {
            return _active.GetValueOrDefault(taskId);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal interface IWindowsFolderLockServiceTestHook
{
    void BeforeAclWrite();

    void AfterAclWrite();
}
