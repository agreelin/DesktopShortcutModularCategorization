using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Broker.Transport;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Services;

namespace FolderSessionLock.Broker;

internal sealed class BrokerCommandProcessor
{
    private readonly WindowsFolderPathValidator _pathValidator;
    private readonly LockTaskManager _taskManager;
    private readonly LockTaskCoordinator _coordinator;
    private readonly IFolderLockService _folderLockService;
    private readonly RecoveryTaskRegistry _recoveryRegistry;
    private readonly IClock _clock;
    private readonly LockDurationPolicy _durationPolicy;
    private readonly RecoveryCreateLockGate _createLockGate;

    internal BrokerCommandProcessor(
        WindowsFolderPathValidator pathValidator,
        LockTaskManager taskManager,
        LockTaskCoordinator coordinator,
        IFolderLockService folderLockService,
        RecoveryTaskRegistry recoveryRegistry,
        IClock clock,
        LockDurationPolicy durationPolicy,
        RecoveryCreateLockGate createLockGate)
    {
        _pathValidator = pathValidator;
        _taskManager = taskManager;
        _coordinator = coordinator;
        _folderLockService = folderLockService;
        _recoveryRegistry = recoveryRegistry;
        _clock = clock;
        _durationPolicy = durationPolicy;
        _createLockGate = createLockGate ?? throw new ArgumentNullException(nameof(createLockGate));
    }

    internal async ValueTask<BrokerExecutionOutcome> ProcessAsync(
        BrokerRequestEnvelope request,
        BrokerExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        BrokerPermissionDecision permission = BrokerPermissionPolicy.Evaluate(
            executionContext,
            request.Command);
        if (!permission.IsAllowed)
        {
            return Failed(request, permission.Error!, BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        try
        {
            return request.Command switch
            {
                BrokerCommand.ValidatePath => ValidatePath(request),
                BrokerCommand.CreateLock => await CreateLockAsync(request, cancellationToken),
                BrokerCommand.RemoveLock => await RemoveLockAsync(
                    request,
                    permission.RemovalIntent!.Value,
                    cancellationToken),
                BrokerCommand.GetStatus => GetStatus(request),
                _ => Failed(
                    request,
                    new BrokerError(
                        BrokerErrorCodes.FSL_E_UNKNOWN_COMMAND,
                        BrokerErrorCodes.FSL_E_UNKNOWN_COMMAND,
                        false,
                        "command"),
                    BrokerExecutionEffect.FailedWithoutSideEffects),
            };
        }
        catch (OperationCanceledException)
        {
            return Failed(
                request,
                new BrokerError(
                    BrokerErrorCodes.FSL_E_OPERATION_CANCELLED,
                    BrokerErrorCodes.FSL_E_OPERATION_CANCELLED,
                    true,
                    null),
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }
        catch (Exception)
        {
            return Failed(request, BrokerError.Internal(), BrokerExecutionEffect.RecoveryRequired);
        }
    }

    private BrokerExecutionOutcome ValidatePath(BrokerRequestEnvelope request)
    {
        var payload = (ValidatePathRequest)request.Payload;
        Result<ValidatedDirectory> validation = _pathValidator.Validate(payload.Path);
        if (validation.IsFailure)
        {
            return Failed(
                request,
                MapError(validation.Error!, "payload.path"),
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        using ValidatedDirectory directory = validation.Value;
        return Succeeded(request, new ValidatePathResult(
            directory.NormalizedPath,
            Path.GetPathRoot(directory.NormalizedPath)!,
            directory.Identity.VolumeSerialNumberText,
            directory.Identity.FileIdHighText,
            directory.Identity.FileIdLowText,
            "NTFS",
            "Fixed",
            false,
            true));
    }

    private async ValueTask<BrokerExecutionOutcome> CreateLockAsync(
        BrokerRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        BrokerError? readinessError = await _createLockGate.CheckAsync(cancellationToken);
        if (readinessError is not null)
        {
            return Failed(
                request,
                readinessError,
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        var payload = (CreateLockRequest)request.Payload;
        Result<CreateLockDomainValues> domain = payload.ToDomain(_durationPolicy);
        if (domain.IsFailure)
        {
            return Failed(
                request,
                MapError(domain.Error!, "payload.durationMilliseconds"),
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        if (!_recoveryRegistry.BeginRequest(request.RequestId, payload.TaskId))
        {
            return Failed(
                request,
                Error(BrokerErrorCodes.FSL_E_TASK_ID_CONFLICT, "payload.taskId"),
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        Result<FolderLockTask> existing = _taskManager.GetById(domain.Value.TaskId);
        if (existing.IsSuccess)
        {
            if (existing.Value.FolderPath != domain.Value.Path
                || existing.Value.Duration != domain.Value.Duration)
            {
                return Failed(
                    request,
                    Error(BrokerErrorCodes.FSL_E_TASK_ID_CONFLICT, "payload.taskId"),
                    BrokerExecutionEffect.FailedWithoutSideEffects);
            }

            RecoveryRecord? existingRecord = _recoveryRegistry.GetByTaskId(payload.TaskId);
            return existing.Value.Status == LockTaskStatus.Active && existingRecord is not null
                ? Succeeded(request, CreateResult(existing.Value, existingRecord.RecordId, true))
                : Failed(
                    request,
                    Error(BrokerErrorCodes.FSL_E_TASK_ID_CONFLICT, "payload.taskId"),
                    BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        Result<FolderLockTask> task = FolderLockTask.Create(
            domain.Value.TaskId,
            domain.Value.Path,
            domain.Value.Duration,
            _clock.UtcNow);
        if (task.IsFailure)
        {
            return Failed(
                request,
                MapError(task.Error!, "payload"),
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        Result add = _taskManager.Add(task.Value);
        if (add.IsFailure)
        {
            return Failed(
                request,
                MapError(add.Error!, "payload.path"),
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        Result<FolderLockTask> activation = await _coordinator.ActivateAsync(
            domain.Value.TaskId,
            cancellationToken);
        if (activation.IsFailure)
        {
            FolderLockTask failedTask = _taskManager.GetById(domain.Value.TaskId).Value;
            BrokerExecutionEffect effect = failedTask.Status == LockTaskStatus.RecoveryRequired
                ? BrokerExecutionEffect.RecoveryRequired
                : activation.Error!.Code == "windows.acl.add_verification_rolled_back"
                    ? BrokerExecutionEffect.RolledBack
                    : BrokerExecutionEffect.FailedWithoutSideEffects;
            return Failed(request, MapError(activation.Error!, null), effect);
        }

        RecoveryRecord? recoveryRecord = _recoveryRegistry.GetByTaskId(payload.TaskId);
        if (recoveryRecord is null || recoveryRecord.State != RecoveryRecordState.Applied)
        {
            return Failed(
                request,
                Error(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED, null),
                BrokerExecutionEffect.RecoveryRequired);
        }

        return Succeeded(request, CreateResult(activation.Value, recoveryRecord.RecordId, false));
    }

    private async ValueTask<BrokerExecutionOutcome> RemoveLockAsync(
        BrokerRequestEnvelope request,
        LockRemovalIntent intent,
        CancellationToken cancellationToken)
    {
        var payload = (RemoveLockRequest)request.Payload;
        RecoveryRecord? recoveryRecord = _recoveryRegistry.GetByRecordId(payload.RecoveryRecordId);
        if (recoveryRecord is null || recoveryRecord.TaskId != payload.TaskId)
        {
            return Failed(
                request,
                Error(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_MISMATCH, "payload.recoveryRecordId"),
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        Result<FolderLockTaskId> taskId = FolderLockTaskId.Create(payload.TaskId);
        Result<FolderLockTask> current = taskId.IsSuccess
            ? _taskManager.GetById(taskId.Value)
            : Result<FolderLockTask>.Failure(taskId.Error!);
        if (current.IsFailure)
        {
            return Failed(
                request,
                Error(BrokerErrorCodes.FSL_E_TASK_NOT_FOUND, "payload.taskId"),
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        LockTaskStatus previousStatus = current.Value.Status;
        Result<LockTaskTransition> unlocking = _taskManager.TryTransition(
            taskId.Value,
            LockTaskStatus.Unlocking,
            _clock.UtcNow,
            removalIntent: intent);
        if (unlocking.IsFailure)
        {
            return Failed(request, MapError(unlocking.Error!, null), BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        Result remove = await _folderLockService.RemoveLockAsync(
            payload.TaskId,
            intent,
            cancellationToken);
        if (remove.IsFailure)
        {
            LockTaskStatus failedStatus = remove.Error!.Category == ErrorCategory.UnrecoverableError
                ? LockTaskStatus.RecoveryRequired
                : LockTaskStatus.UnlockFailed;
            _ = _taskManager.TryTransition(
                taskId.Value,
                failedStatus,
                _clock.UtcNow,
                new LockTaskError(remove.Error, _clock.UtcNow));
            return Failed(
                request,
                MapRemovalError(remove.Error),
                failedStatus == LockTaskStatus.RecoveryRequired
                    ? BrokerExecutionEffect.RecoveryRequired
                    : BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        Result<LockTaskTransition> completed = _taskManager.TryTransition(
            taskId.Value,
            LockTaskStatus.Completed,
            _clock.UtcNow);
        if (completed.IsFailure)
        {
            return Failed(request, MapError(completed.Error!, null), BrokerExecutionEffect.RecoveryRequired);
        }

        return Succeeded(request, new RemoveLockResult(
            payload.TaskId,
            payload.RecoveryRecordId,
            intent,
            previousStatus,
            LockTaskStatus.Completed,
            _clock.UtcNow,
            true,
            _recoveryRegistry.GetByRecordId(payload.RecoveryRecordId) is null,
            false));
    }

    private BrokerExecutionOutcome GetStatus(BrokerRequestEnvelope request)
    {
        var payload = (GetStatusRequest)request.Payload;
        IReadOnlyList<FolderLockTask> tasks = payload.QueryType == GetStatusQueryType.ByTaskId
            ? GetSingle(payload.TaskId!.Value)
            : _taskManager.GetAll();
        if (payload.QueryType == GetStatusQueryType.ByTaskId && tasks.Count == 0)
        {
            return Failed(
                request,
                Error(BrokerErrorCodes.FSL_E_TASK_NOT_FOUND, "payload.taskId"),
                BrokerExecutionEffect.FailedWithoutSideEffects);
        }

        return Succeeded(request, new GetStatusResult(
            payload.QueryType,
            tasks.Select(ToStatusItem).ToArray()));
    }

    private IReadOnlyList<FolderLockTask> GetSingle(Guid taskId)
    {
        Result<FolderLockTaskId> id = FolderLockTaskId.Create(taskId);
        if (id.IsFailure)
        {
            return [];
        }

        Result<FolderLockTask> task = _taskManager.GetById(id.Value);
        return task.IsSuccess ? [task.Value] : [];
    }

    private CreateLockResult CreateResult(FolderLockTask task, Guid recoveryRecordId, bool replay) => new(
        task.Id.Value,
        task.FolderPath.Value,
        LockTaskStatus.Active,
        task.StartedAtUtc!.Value,
        task.ExpectedExpiryUtc!.Value,
        checked((long)task.Duration.Value.TotalMilliseconds),
        RemainingMilliseconds(task),
        recoveryRecordId,
        replay);

    private TaskStatusItem ToStatusItem(FolderLockTask task) => new(
        task.Id.Value,
        task.FolderPath.Value,
        task.Status,
        task.StartedAtUtc,
        task.ExpectedExpiryUtc,
        checked((long)task.Duration.Value.TotalMilliseconds),
        RemainingMilliseconds(task),
        false,
        task.Status == LockTaskStatus.RecoveryRequired,
        task.Error is null
            ? null
            : new TaskStatusError(
                MapCode(task.Error.Detail.Code),
                task.Error.Detail.Code,
                false));

    private long RemainingMilliseconds(FolderLockTask task) =>
        Math.Max(0, checked((long)task.GetRemainingTime(_clock).TotalMilliseconds));

    private BrokerExecutionOutcome Succeeded(
        BrokerRequestEnvelope request,
        IBrokerResult result) => BrokerExecutionOutcome.Succeeded(
            BrokerResponseEnvelope.Succeeded(
                request.RequestId,
                request.Command,
                _clock.UtcNow,
                result));

    private BrokerExecutionOutcome Failed(
        BrokerRequestEnvelope request,
        BrokerError error,
        BrokerExecutionEffect effect)
    {
        BrokerResponseEnvelope response = BrokerResponseEnvelope.Failed(
            request.RequestId,
            request.Command,
            _clock.UtcNow,
            error);
        return effect switch
        {
            BrokerExecutionEffect.RolledBack => BrokerExecutionOutcome.RolledBack(response),
            BrokerExecutionEffect.RecoveryRequired => BrokerExecutionOutcome.RecoveryRequired(response),
            _ => BrokerExecutionOutcome.FailedWithoutSideEffects(response),
        };
    }

    private static BrokerError MapError(Error error, string? field) =>
        Error(MapCode(error.Code), field);

    private static BrokerError MapRemovalError(Error error) => Error(
        error.Code switch
        {
            "windows.acl.native_call_failed" => BrokerErrorCodes.FSL_E_ACL_REMOVE_FAILED,
            "windows.acl.verification_failed" => BrokerErrorCodes.FSL_E_ACL_POST_VERIFY_FAILED,
            _ => MapCode(error.Code),
        },
        null);

    private static BrokerError Error(string code, string? field) => new(code, code, false, field);

    private static string MapCode(string code) => code switch
    {
        "windows.path.empty" => BrokerErrorCodes.FSL_E_PATH_EMPTY,
        "windows.path.relative" => BrokerErrorCodes.FSL_E_PATH_NOT_ABSOLUTE,
        "windows.path.invalid" => BrokerErrorCodes.FSL_E_PATH_INVALID,
        "windows.path.not_found" => BrokerErrorCodes.FSL_E_PATH_NOT_FOUND,
        "windows.path.not_directory" => BrokerErrorCodes.FSL_E_PATH_NOT_DIRECTORY,
        "windows.path.root" => BrokerErrorCodes.FSL_E_PATH_ROOT_FORBIDDEN,
        "windows.path.protected" => BrokerErrorCodes.FSL_E_PATH_NOT_ALLOWED,
        "windows.path.unc" => BrokerErrorCodes.FSL_E_PATH_NETWORK_FORBIDDEN,
        "windows.path.drive_not_fixed" => BrokerErrorCodes.FSL_E_PATH_DRIVE_TYPE_UNSUPPORTED,
        "windows.path.file_system_not_ntfs" => BrokerErrorCodes.FSL_E_PATH_FILESYSTEM_UNSUPPORTED,
        "windows.path.reparse_point" => BrokerErrorCodes.FSL_E_PATH_REPARSE_POINT_FORBIDDEN,
        "windows.path.insufficient_permissions" => BrokerErrorCodes.FSL_E_PATH_ACCESS_DENIED,
        "windows.path.mapping_changed" => BrokerErrorCodes.FSL_E_PATH_IDENTITY_CHANGED,
        "windows.lock.task_id_conflict" => BrokerErrorCodes.FSL_E_TASK_ID_CONFLICT,
        "windows.lock.path_conflict" => BrokerErrorCodes.FSL_E_PATH_ALREADY_LOCKED,
        "windows.lock.task_not_found" => BrokerErrorCodes.FSL_E_TASK_NOT_FOUND,
        "windows.lock.acl_state_unknown" => BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
        "windows.acl.identical_ace_exists" => BrokerErrorCodes.FSL_E_ACL_STATE_MISMATCH,
        "windows.acl.add_verification_rolled_back" => BrokerErrorCodes.FSL_E_ACL_POST_VERIFY_FAILED,
        "windows.acl.verification_failed" => BrokerErrorCodes.FSL_E_ACL_POST_VERIFY_FAILED,
        "windows.acl.native_call_failed" => BrokerErrorCodes.FSL_E_ACL_APPLY_FAILED,
        "lock_task.duration.out_of_range" => BrokerErrorCodes.FSL_E_DURATION_OUT_OF_RANGE,
        _ when code.StartsWith("FSL_E_", StringComparison.Ordinal) => code,
        _ => BrokerErrorCodes.FSL_E_INTERNAL,
    };
}
