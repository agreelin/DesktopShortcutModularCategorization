using System.Text.Json.Serialization;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Protocol;

public sealed record ValidatePathRequest(
    [property: JsonPropertyName("path")] string Path) : IBrokerRequestPayload;

public sealed record ValidatePathResult(
    [property: JsonPropertyName("normalizedPath")] string NormalizedPath,
    [property: JsonPropertyName("volumeRoot")] string VolumeRoot,
    [property: JsonPropertyName("volumeSerialNumber")] string VolumeSerialNumber,
    [property: JsonPropertyName("fileIdHigh")] string FileIdHigh,
    [property: JsonPropertyName("fileIdLow")] string FileIdLow,
    [property: JsonPropertyName("fileSystem")] string FileSystem,
    [property: JsonPropertyName("driveType")] string DriveType,
    [property: JsonPropertyName("isReparsePoint")] bool IsReparsePoint,
    [property: JsonPropertyName("isAllowed")] bool IsAllowed) : IBrokerResult;

public sealed record CreateLockRequest(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("durationMilliseconds")] long DurationMilliseconds) : IBrokerRequestPayload
{
    public Result<CreateLockDomainValues> ToDomain(LockDurationPolicy durationPolicy)
    {
        ArgumentNullException.ThrowIfNull(durationPolicy);

        Result<FolderLockTaskId> taskId = FolderLockTaskId.Create(TaskId);
        if (taskId.IsFailure)
        {
            return Result<CreateLockDomainValues>.Failure(taskId.Error!);
        }

        Result<FolderPath> path = FolderPath.Create(Path);
        if (path.IsFailure)
        {
            return Result<CreateLockDomainValues>.Failure(path.Error!);
        }

        TimeSpan duration;
        try
        {
            duration = TimeSpan.FromTicks(checked(DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        }
        catch (OverflowException)
        {
            return Result<CreateLockDomainValues>.Failure(DurationOutOfRange());
        }

        Result<LockDuration> lockDuration = LockDuration.Create(duration, durationPolicy);
        return lockDuration.IsSuccess
            ? Result<CreateLockDomainValues>.Success(new CreateLockDomainValues(
                taskId.Value,
                path.Value,
                lockDuration.Value))
            : Result<CreateLockDomainValues>.Failure(DurationOutOfRange());
    }

    private static Error DurationOutOfRange() => new(
        BrokerErrorCodes.FSL_E_DURATION_OUT_OF_RANGE,
        "The lock duration is outside the allowed range.",
        ErrorCategory.ValidationFailed);
}

public sealed record CreateLockDomainValues(
    FolderLockTaskId TaskId,
    FolderPath Path,
    LockDuration Duration);

public sealed record CreateLockResult(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("normalizedPath")] string NormalizedPath,
    [property: JsonPropertyName("status")] LockTaskStatus Status,
    [property: JsonPropertyName("startedUtc")] DateTimeOffset StartedUtc,
    [property: JsonPropertyName("expiresUtc")] DateTimeOffset ExpiresUtc,
    [property: JsonPropertyName("durationMilliseconds")] long DurationMilliseconds,
    [property: JsonPropertyName("remainingMilliseconds")] long RemainingMilliseconds,
    [property: JsonPropertyName("recoveryRecordId")] Guid RecoveryRecordId,
    [property: JsonPropertyName("idempotentReplay")] bool IdempotentReplay) : IBrokerResult;

public sealed record RemoveLockRequest(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("recoveryRecordId")] Guid RecoveryRecordId) : IBrokerRequestPayload;

public sealed record RemoveLockResult(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("recoveryRecordId")] Guid RecoveryRecordId,
    [property: JsonPropertyName("removalIntent")] LockRemovalIntent RemovalIntent,
    [property: JsonPropertyName("previousStatus")] LockTaskStatus PreviousStatus,
    [property: JsonPropertyName("status")] LockTaskStatus Status,
    [property: JsonPropertyName("removedUtc")] DateTimeOffset RemovedUtc,
    [property: JsonPropertyName("aceRemoved")] bool AceRemoved,
    [property: JsonPropertyName("recoveryRecordDeleted")] bool RecoveryRecordDeleted,
    [property: JsonPropertyName("idempotentReplay")] bool IdempotentReplay) : IBrokerResult;

public enum GetStatusQueryType
{
    ByTaskId,
    CurrentSession,
}

public sealed record GetStatusRequest(
    [property: JsonPropertyName("queryType")] GetStatusQueryType QueryType,
    [property: JsonPropertyName("taskId")] Guid? TaskId) : IBrokerRequestPayload;

public sealed record GetStatusResult(
    [property: JsonPropertyName("queryType")] GetStatusQueryType QueryType,
    [property: JsonPropertyName("tasks")] IReadOnlyList<TaskStatusItem> Tasks) : IBrokerResult;

public sealed record TaskStatusItem(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("normalizedPath")] string NormalizedPath,
    [property: JsonPropertyName("status")] LockTaskStatus Status,
    [property: JsonPropertyName("startedUtc")] DateTimeOffset? StartedUtc,
    [property: JsonPropertyName("expiresUtc")] DateTimeOffset? ExpiresUtc,
    [property: JsonPropertyName("durationMilliseconds")] long DurationMilliseconds,
    [property: JsonPropertyName("remainingMilliseconds")] long RemainingMilliseconds,
    [property: JsonPropertyName("canUserRemove")] bool CanUserRemove,
    [property: JsonPropertyName("recoveryRequired")] bool RecoveryRequired,
    [property: JsonPropertyName("error")] TaskStatusError? Error);

public sealed record TaskStatusError
{
    public TaskStatusError(string code, string message, bool retryable)
    {
        if (!BrokerProtocolValidation.IsErrorCode(code))
        {
            throw new ArgumentException("The error code does not use the protocol format.", nameof(code));
        }

        ArgumentNullException.ThrowIfNull(message);
        if (message.Length > BrokerProtocolConstants.MaximumErrorMessageLength)
        {
            throw new ArgumentException("The error message exceeds the protocol limit.", nameof(message));
        }

        Code = code;
        Message = message;
        Retryable = retryable;
    }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; }
}
