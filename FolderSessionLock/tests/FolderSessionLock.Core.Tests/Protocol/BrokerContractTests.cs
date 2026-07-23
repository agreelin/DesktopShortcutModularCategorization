using System.Reflection;
using System.Text.Json;
using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Protocol.Tests;

public sealed class BrokerContractTests
{
    [Fact]
    public void Commands_AreTheExactClosedProtocolSet()
    {
        string[] expected = ["ValidatePath", "CreateLock", "RemoveLock", "GetStatus"];

        Assert.Equal(expected, BrokerProtocolConstants.Commands);
        Assert.Equal(expected, Enum.GetNames<BrokerCommand>());
        Assert.Equal(4, Enum.GetValues<BrokerCommand>().Length);
    }

    [Fact]
    public void ErrorCodes_AreTheExactD027Set()
    {
        string[] expected =
        [
            "FSL_E_ACCOUNT_SID_MISMATCH",
            "FSL_E_ACL_APPLY_FAILED",
            "FSL_E_ACL_POST_VERIFY_FAILED",
            "FSL_E_ACL_REMOVE_FAILED",
            "FSL_E_ACL_ROLLBACK_FAILED",
            "FSL_E_ACL_STATE_MISMATCH",
            "FSL_E_BROKER_CONNECT_TIMEOUT",
            "FSL_E_BROKER_EXITED_EARLY",
            "FSL_E_BROKER_LAUNCH_CONTRACT_INVALID",
            "FSL_E_BROKER_PATH_UNTRUSTED",
            "FSL_E_BROKER_PROCESS_CLEANUP_FAILED",
            "FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED",
            "FSL_E_CLIENT_IDENTITY_UNAVAILABLE",
            "FSL_E_CLIENT_PROCESS_MISMATCH",
            "FSL_E_DURATION_OUT_OF_RANGE",
            "FSL_E_ELEVATION_CANCELLED",
            "FSL_E_ELEVATION_LAUNCH_FAILED",
            "FSL_E_FORBIDDEN_INPUT",
            "FSL_E_HANDSHAKE_EXPIRED",
            "FSL_E_HANDSHAKE_REQUIRED",
            "FSL_E_HANDSHAKE_VERSION_UNSUPPORTED",
            "FSL_E_INTERNAL",
            "FSL_E_INVALID_ARGUMENTS",
            "FSL_E_MALFORMED_MESSAGE",
            "FSL_E_OPERATION_CANCELLED",
            "FSL_E_LOGON_SID_MISMATCH",
            "FSL_E_PATH_ACCESS_DENIED",
            "FSL_E_PATH_ALREADY_LOCKED",
            "FSL_E_PATH_APPLICATION_FORBIDDEN",
            "FSL_E_PATH_DRIVE_TYPE_UNSUPPORTED",
            "FSL_E_PATH_EMPTY",
            "FSL_E_PATH_FILESYSTEM_UNSUPPORTED",
            "FSL_E_PATH_IDENTITY_CHANGED",
            "FSL_E_PATH_IDENTITY_UNAVAILABLE",
            "FSL_E_PATH_INVALID",
            "FSL_E_PATH_NETWORK_FORBIDDEN",
            "FSL_E_PATH_NOT_ABSOLUTE",
            "FSL_E_PATH_NOT_ALLOWED",
            "FSL_E_PATH_NOT_DIRECTORY",
            "FSL_E_PATH_NOT_FOUND",
            "FSL_E_PATH_OVERLAP",
            "FSL_E_PATH_REPARSE_POINT_FORBIDDEN",
            "FSL_E_PATH_REPOSITORY_FORBIDDEN",
            "FSL_E_PATH_ROOT_FORBIDDEN",
            "FSL_E_PATH_SYSTEM_FORBIDDEN",
            "FSL_E_PATH_SYNCHRONIZATION_ROOT_FORBIDDEN",
            "FSL_E_PATH_USER_PROFILE_ROOT_FORBIDDEN",
            "FSL_E_PIPE_ACCESS_DENIED",
            "FSL_E_PIPE_INITIALIZATION_FAILED",
            "FSL_E_PROTOCOL_VERSION_UNSUPPORTED",
            "FSL_E_PROTOCOL_SEQUENCE_INVALID",
            "FSL_E_PROTECTED_PATH_DACL_MISMATCH",
            "FSL_E_PROTECTED_PATH_DACL_MISSING",
            "FSL_E_PROTECTED_PATH_DACL_NULL",
            "FSL_E_PROTECTED_PATH_FINAL_PATH_MISMATCH",
            "FSL_E_PROTECTED_PATH_IDENTITY_CHANGED",
            "FSL_E_PROTECTED_PATH_IDENTITY_UNAVAILABLE",
            "FSL_E_PROTECTED_PATH_INHERITANCE_INVALID",
            "FSL_E_PROTECTED_PATH_NOT_FOUND",
            "FSL_E_PROTECTED_PATH_OPEN_FAILED",
            "FSL_E_PROTECTED_PATH_OWNER_MISMATCH",
            "FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED",
            "FSL_E_PROTECTED_PATH_REPARSE_POINT",
            "FSL_E_PROTECTED_PATH_SECURITY_READ_FAILED",
            "FSL_E_PROTECTED_PATH_VOLUME_UNSUPPORTED",
            "FSL_E_PROTECTED_LOG_ARTIFACT_INVALID",
            "FSL_E_PROTECTED_LOGGER_UNAVAILABLE",
            "FSL_E_RECOVERY_ARTIFACT_INVALID",
            "FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID",
            "FSL_E_RECOVERY_BACKUP_ORPHANED",
            "FSL_E_RECOVERY_BLOCKING",
            "FSL_E_RECOVERY_DIRECTORY_ENUMERATION_FAILED",
            "FSL_E_RECOVERY_DIRECTORY_OPEN_FAILED",
            "FSL_E_RECOVERY_ENTRY_METADATA_FAILED",
            "FSL_E_RECOVERY_FILE_ALREADY_EXISTS",
            "FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED",
            "FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED",
            "FSL_E_RECOVERY_FILE_DACL_MISMATCH",
            "FSL_E_RECOVERY_FILE_DACL_MISSING",
            "FSL_E_RECOVERY_FILE_DACL_NULL",
            "FSL_E_RECOVERY_FILE_DACL_SET_FAILED",
            "FSL_E_RECOVERY_FILE_DELETE_FAILED",
            "FSL_E_RECOVERY_FILE_HANDLE_DELETE_UNSUPPORTED",
            "FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH",
            "FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED",
            "FSL_E_RECOVERY_FILE_INHERITANCE_INVALID",
            "FSL_E_RECOVERY_FILE_OWNER_MISMATCH",
            "FSL_E_RECOVERY_FILE_OWNER_PRIVILEGE_UNAVAILABLE",
            "FSL_E_RECOVERY_FILE_OWNER_SET_FAILED",
            "FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED",
            "FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED",
            "FSL_E_RECOVERY_FILE_SECURITY_READ_FAILED",
            "FSL_E_RECOVERY_FILE_SERVICE_SID_UNAVAILABLE",
            "FSL_E_RECOVERY_RECORD_DELETE_FAILED",
            "FSL_E_RECOVERY_RECORD_FLAGS_UNSUPPORTED",
            "FSL_E_RECOVERY_RECORD_ID_MISMATCH",
            "FSL_E_RECOVERY_RECORD_LENGTH_INVALID",
            "FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED",
            "FSL_E_RECOVERY_RECORD_MAGIC_INVALID",
            "FSL_E_RECOVERY_RECORD_MISMATCH",
            "FSL_E_RECOVERY_RECORD_NOT_FOUND",
            "FSL_E_RECOVERY_RECORD_TRAILING_DATA",
            "FSL_E_RECOVERY_RECORD_TRUNCATED",
            "FSL_E_RECOVERY_RECORD_UNPROTECT_FAILED",
            "FSL_E_RECOVERY_RECORD_VERSION_UNSUPPORTED",
            "FSL_E_RECOVERY_RECORD_WRITE_FAILED",
            "FSL_E_RECOVERY_ACCESS_MASK_UNSUPPORTED",
            "FSL_E_RECOVERY_PAYLOAD_MALFORMED",
            "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID",
            "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID",
            "FSL_E_RECOVERY_PAYLOAD_TOO_LARGE",
            "FSL_E_RECOVERY_PAYLOAD_VERSION_UNSUPPORTED",
            "FSL_E_RECOVERY_REQUIRED",
            "FSL_E_RECOVERY_READINESS_ARTIFACT_INVALID",
            "FSL_E_RECOVERY_READINESS_DELETE_FAILED",
            "FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED",
            "FSL_E_RECOVERY_READINESS_NOT_FOUND",
            "FSL_E_RECOVERY_READINESS_OPEN_FAILED",
            "FSL_E_RECOVERY_READINESS_PUBLISH_FAILED",
            "FSL_E_RECOVERY_READINESS_SCHEMA_INVALID",
            "FSL_E_RECOVERY_READINESS_SECURITY_INVALID",
            "FSL_E_RECOVERY_READINESS_STALE",
            "FSL_E_RECOVERY_READINESS_VERSION_UNSUPPORTED",
            "FSL_E_RECOVERY_TEMP_ORPHANED",
            "FSL_E_RECOVERY_TEMP_CLEANUP_FAILED",
            "FSL_E_REPLAY_DETECTED",
            "FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE",
            "FSL_E_REQUEST_BINDING_MISMATCH",
            "FSL_E_REQUEST_EXPIRED",
            "FSL_E_REQUEST_IN_PROGRESS",
            "FSL_E_SCHEMA_VIOLATION",
            "FSL_E_SESSION_MISMATCH",
            "FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE",
            "FSL_E_TASK_ID_CONFLICT",
            "FSL_E_TASK_NOT_FOUND",
            "FSL_E_UNAUTHORIZED_CALLER",
            "FSL_E_UNKNOWN_COMMAND",
        ];
        string[] actual = typeof(BrokerErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(137, actual.Length);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
        Assert.All(actual, code => Assert.Matches("^FSL_E_[A-Z0-9]+(?:_[A-Z0-9]+)*$", code));
    }

    [Fact]
    public void SerializeRequest_WritesExactlySixEnvelopeProperties()
    {
        var request = new BrokerRequestEnvelope(
            1,
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            1,
            ProtocolTestData.ServerTimeUtc,
            new ValidatePathRequest(@"C:\Data\Locked"));

        using JsonDocument document = JsonDocument.Parse(BrokerProtocolJson.SerializeRequest(request));

        Assert.Equal(
            ["protocolVersion", "requestId", "command", "clientSessionId", "sentAtUtc", "payload"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void SerializeResponse_WritesExactlySevenEnvelopeAndFourErrorProperties()
    {
        BrokerResponseEnvelope response = BrokerResponseEnvelope.Failed(
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            ProtocolTestData.ServerTimeUtc,
            new BrokerError(BrokerErrorCodes.FSL_E_PATH_EMPTY, "A folder path is required.", false, "payload.path"));

        using JsonDocument document = JsonDocument.Parse(BrokerProtocolJson.SerializeResponse(response));
        JsonElement root = document.RootElement;

        Assert.Equal(
            ["protocolVersion", "requestId", "command", "success", "serverTimeUtc", "result", "error"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["code", "message", "retryable", "field"],
            root.GetProperty("error").EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("result").ValueKind);
    }

    [Fact]
    public void ResponseFactories_EnforceSuccessAndFailureNullInvariants()
    {
        BrokerResponseEnvelope success = BrokerResponseEnvelope.Succeeded(
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            ProtocolTestData.ServerTimeUtc,
            new ValidatePathResult(@"C:\Data\Locked", @"C:\", "0123456789abcdef", "1", "2", "NTFS", "Fixed", false, true));
        BrokerResponseEnvelope failure = BrokerResponseEnvelope.Failed(
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            ProtocolTestData.ServerTimeUtc,
            BrokerError.Internal());

        Assert.NotNull(success.Result);
        Assert.Null(success.Error);
        Assert.Null(failure.Result);
        Assert.NotNull(failure.Error);
        Assert.Throws<ArgumentNullException>(() => BrokerResponseEnvelope.Succeeded(
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            ProtocolTestData.ServerTimeUtc,
            null!));
        Assert.Throws<ArgumentNullException>(() => BrokerResponseEnvelope.Failed(
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            ProtocolTestData.ServerTimeUtc,
            null!));
    }

    [Fact]
    public void CreateLockRequest_ToDomain_UsesCallerProvidedDurationPolicy()
    {
        var request = new CreateLockRequest(
            ProtocolTestData.TaskId,
            @"C:\Data\Locked",
            120_000);
        LockDurationPolicy accepting = LockDurationPolicy.Create(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3)).Value;
        LockDurationPolicy rejecting = LockDurationPolicy.Create(
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(4)).Value;

        var accepted = request.ToDomain(accepting);
        var rejected = request.ToDomain(rejecting);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(ProtocolTestData.TaskId, accepted.Value.TaskId.Value);
        Assert.Equal(Path.GetFullPath(@"C:\Data\Locked"), accepted.Value.Path.Value);
        Assert.Equal(TimeSpan.FromMinutes(2), accepted.Value.Duration.Value);
        Assert.True(rejected.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_DURATION_OUT_OF_RANGE, rejected.Error!.Code);
    }

    [Fact]
    public void PermissionPolicy_RejectsUiRemoveAndMapsOnlyInternalRemovalIntents()
    {
        BrokerPermissionDecision ui = BrokerPermissionPolicy.Evaluate(
            BrokerExecutionContext.OrdinaryUi,
            BrokerCommand.RemoveLock);
        BrokerPermissionDecision scheduler = BrokerPermissionPolicy.Evaluate(
            BrokerExecutionContext.ConsentBrokerInternalScheduler,
            BrokerCommand.RemoveLock);
        BrokerPermissionDecision recoveryService = BrokerPermissionPolicy.Evaluate(
            BrokerExecutionContext.RecoveryService,
            BrokerCommand.RemoveLock);
        BrokerPermissionDecision recoveryOnce = BrokerPermissionPolicy.Evaluate(
            BrokerExecutionContext.RecoveryOnce,
            BrokerCommand.RemoveLock);
        BrokerPermissionDecision cleanup = BrokerPermissionPolicy.Evaluate(
            BrokerExecutionContext.TestCleanup,
            BrokerCommand.RemoveLock);

        Assert.False(ui.IsAllowed);
        Assert.Equal(BrokerErrorCodes.FSL_E_UNAUTHORIZED_CALLER, ui.Error!.Code);
        Assert.Equal(LockRemovalIntent.Expiration, scheduler.RemovalIntent);
        Assert.Equal(LockRemovalIntent.Recovery, recoveryService.RemovalIntent);
        Assert.Equal(LockRemovalIntent.Recovery, recoveryOnce.RemovalIntent);
        Assert.Equal(LockRemovalIntent.TestCleanup, cleanup.RemovalIntent);
        Assert.DoesNotContain(
            LockRemovalIntent.AdministrativeCleanup,
            new[] { scheduler.RemovalIntent, recoveryService.RemovalIntent, recoveryOnce.RemovalIntent, cleanup.RemovalIntent });
    }

    [Fact]
    public void ErrorContracts_RejectInvalidCodesAndOversizedMessages()
    {
        string oversized = new('x', BrokerProtocolConstants.MaximumErrorMessageLength + 1);

        Assert.Throws<ArgumentException>(() => new BrokerError("fsl_e_internal", "message", false, null));
        Assert.Throws<ArgumentException>(() => new BrokerError("FSL_E_BAD__CODE", "message", false, null));
        Assert.Throws<ArgumentException>(() => new BrokerError(BrokerErrorCodes.FSL_E_INTERNAL, oversized, false, null));
        Assert.Throws<ArgumentException>(() => new TaskStatusError("FSL_E_", "message", false));
        Assert.Throws<ArgumentException>(() => new TaskStatusError(BrokerErrorCodes.FSL_E_INTERNAL, oversized, false));
    }

    [Fact]
    public void InternalError_UsesTheFixedPublicMessage()
    {
        BrokerError error = BrokerError.Internal();

        Assert.Equal(BrokerErrorCodes.FSL_E_INTERNAL, error.Code);
        Assert.Equal("The operation could not be completed.", error.Message);
        Assert.False(error.Retryable);
        Assert.Null(error.Field);
    }

    [Fact]
    public void Serializers_RejectMismatchedCommandContractsAndUnsupportedRemovalIntent()
    {
        var mismatchedRequest = new BrokerRequestEnvelope(
            1,
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            1,
            ProtocolTestData.ServerTimeUtc,
            new GetStatusRequest(GetStatusQueryType.CurrentSession, null));

        Assert.Throws<ArgumentException>(() => BrokerProtocolJson.SerializeRequest(mismatchedRequest));
        Assert.Throws<ArgumentException>(() => BrokerResponseEnvelope.Succeeded(
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            ProtocolTestData.ServerTimeUtc,
            new GetStatusResult(GetStatusQueryType.CurrentSession, [])));

        BrokerResponseEnvelope unsupportedIntent = BrokerResponseEnvelope.Succeeded(
            ProtocolTestData.RequestId,
            BrokerCommand.RemoveLock,
            ProtocolTestData.ServerTimeUtc,
            new RemoveLockResult(
                ProtocolTestData.TaskId,
                ProtocolTestData.RecoveryRecordId,
                LockRemovalIntent.AdministrativeCleanup,
                LockTaskStatus.Active,
                LockTaskStatus.Completed,
                ProtocolTestData.ServerTimeUtc,
                true,
                true,
                false));

        Assert.Throws<ArgumentException>(() => BrokerProtocolJson.SerializeResponse(unsupportedIntent));
    }
}
