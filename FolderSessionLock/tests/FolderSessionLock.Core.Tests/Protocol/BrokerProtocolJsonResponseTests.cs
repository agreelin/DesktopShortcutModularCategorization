using System.Text.Json;
using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Protocol.Tests;

public sealed class BrokerProtocolJsonResponseTests
{
    [Fact]
    public void SerializeAndDeserializeResponse_PreservesExactResultSchemasForAllFourCommands()
    {
        BrokerResponseEnvelope[] responses =
        [
            Success(
                BrokerCommand.ValidatePath,
                new ValidatePathResult(@"C:\Data\Locked", @"C:\", "0123456789abcdef", "1", "2", "NTFS", "Fixed", false, true)),
            Success(
                BrokerCommand.CreateLock,
                new CreateLockResult(
                    ProtocolTestData.TaskId,
                    @"C:\Data\Locked",
                    LockTaskStatus.Active,
                    ProtocolTestData.ServerTimeUtc,
                    ProtocolTestData.ServerTimeUtc.AddHours(1),
                    3_600_000,
                    3_600_000,
                    ProtocolTestData.RecoveryRecordId,
                    false)),
            Success(
                BrokerCommand.RemoveLock,
                new RemoveLockResult(
                    ProtocolTestData.TaskId,
                    ProtocolTestData.RecoveryRecordId,
                    LockRemovalIntent.Expiration,
                    LockTaskStatus.Active,
                    LockTaskStatus.Completed,
                    ProtocolTestData.ServerTimeUtc,
                    true,
                    true,
                    false)),
            Success(
                BrokerCommand.GetStatus,
                new GetStatusResult(
                    GetStatusQueryType.CurrentSession,
                    [new TaskStatusItem(
                        ProtocolTestData.TaskId,
                        @"C:\Data\Locked",
                        LockTaskStatus.Active,
                        ProtocolTestData.ServerTimeUtc,
                        ProtocolTestData.ServerTimeUtc.AddHours(1),
                        3_600_000,
                        3_600_000,
                        false,
                        false,
                        null)])),
        ];
        string[][] expectedResultProperties =
        [
            ["normalizedPath", "volumeRoot", "volumeSerialNumber", "fileIdHigh", "fileIdLow", "fileSystem", "driveType", "isReparsePoint", "isAllowed"],
            ["taskId", "normalizedPath", "status", "startedUtc", "expiresUtc", "durationMilliseconds", "remainingMilliseconds", "recoveryRecordId", "idempotentReplay"],
            ["taskId", "recoveryRecordId", "removalIntent", "previousStatus", "status", "removedUtc", "aceRemoved", "recoveryRecordDeleted", "idempotentReplay"],
            ["queryType", "tasks"],
        ];

        for (int index = 0; index < responses.Length; index++)
        {
            byte[] json = BrokerProtocolJson.SerializeResponse(responses[index]);
            using JsonDocument document = JsonDocument.Parse(json);
            BrokerResponseParseResult parsed = BrokerProtocolJson.DeserializeResponse(json);

            Assert.Equal(
                expectedResultProperties[index],
                document.RootElement.GetProperty("result").EnumerateObject().Select(property => property.Name));
            Assert.True(
                parsed.IsSuccess,
                $"Response index {index} failed with {parsed.Error?.Code} at {parsed.Error?.Field}.");
            Assert.True(parsed.Response!.Success);
            Assert.Null(parsed.Response.Error);
        }
    }

    [Fact]
    public void DeserializeResponse_RejectsSuccessAndFailureNullInvariantViolations()
    {
        string successWithError = """
            {"protocolVersion":1,"requestId":"11111111-2222-3333-4444-555555555555","command":"ValidatePath","success":true,"serverTimeUtc":"2026-07-19T16:30:00.0000000Z","result":{"normalizedPath":"C:\\Data\\Locked","volumeRoot":"C:\\","volumeSerialNumber":"0123456789abcdef","fileIdHigh":"1","fileIdLow":"2","fileSystem":"NTFS","driveType":"Fixed","isReparsePoint":false,"isAllowed":true},"error":{"code":"FSL_E_INTERNAL","message":"The operation could not be completed.","retryable":false,"field":null}}
            """;
        string failureWithResult = """
            {"protocolVersion":1,"requestId":"11111111-2222-3333-4444-555555555555","command":"ValidatePath","success":false,"serverTimeUtc":"2026-07-19T16:30:00.0000000Z","result":{},"error":{"code":"FSL_E_INTERNAL","message":"The operation could not be completed.","retryable":false,"field":null}}
            """;

        AssertResponseFailure(successWithError, BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
        AssertResponseFailure(failureWithResult, BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
    }

    [Fact]
    public void DeserializeResponse_RejectsDuplicateExtraMissingAndNullFields()
    {
        string duplicate = """
            {"protocolVersion":1,"requestId":"11111111-2222-3333-4444-555555555555","command":"ValidatePath","success":false,"success":true,"serverTimeUtc":"2026-07-19T16:30:00.0000000Z","result":null,"error":{"code":"FSL_E_INTERNAL","message":"The operation could not be completed.","retryable":false,"field":null}}
            """;
        string extra = FailureJson().Replace(
            "\"error\":",
            "\"unexpected\":true,\"error\":",
            StringComparison.Ordinal);
        string missing = FailureJson().Replace("\"result\":null,", string.Empty, StringComparison.Ordinal);
        string nullSuccess = FailureJson().Replace("\"success\":false", "\"success\":null", StringComparison.Ordinal);

        AssertResponseFailure(duplicate, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
        AssertResponseFailure(extra, BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
        AssertResponseFailure(missing, BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
        AssertResponseFailure(nullSuccess, BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
    }

    [Theory]
    [InlineData("A0B1C2D3-E4F5-4678-9123-ABCDEFABCDEF")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void DeserializeResponse_RejectsInvalidGuidWithoutThrowing(string taskId)
    {
        BrokerResponseEnvelope response = Success(
            BrokerCommand.CreateLock,
            new CreateLockResult(
                ProtocolTestData.TaskId,
                @"C:\Data\Locked",
                LockTaskStatus.Active,
                ProtocolTestData.ServerTimeUtc,
                ProtocolTestData.ServerTimeUtc.AddHours(1),
                3_600_000,
                3_600_000,
                ProtocolTestData.RecoveryRecordId,
                false));
        string json = System.Text.Encoding.UTF8.GetString(BrokerProtocolJson.SerializeResponse(response))
            .Replace(ProtocolTestData.TaskId.ToString("D"), taskId, StringComparison.Ordinal);

        AssertResponseFailure(json, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
    }

    [Theory]
    [InlineData("2026-07-19T16:30:00Z")]
    [InlineData("2026-07-19T16:30:00.0000000+00:00")]
    public void DeserializeResponse_RejectsInvalidTimestampWithoutThrowing(string timestamp)
    {
        string json = FailureJson().Replace(
            "2026-07-19T16:30:00.0000000Z",
            timestamp,
            StringComparison.Ordinal);

        AssertResponseFailure(json, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("fsl_e_internal")]
    [InlineData("FSL_E_BAD__CODE")]
    public void DeserializeResponse_RejectsInvalidErrorCodeAsSchemaViolation(string code)
    {
        string json = FailureJson().Replace("FSL_E_INTERNAL", code, StringComparison.Ordinal);

        AssertResponseFailure(json, BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
    }

    [Fact]
    public void DeserializeResponse_RejectsOversizedErrorMessageAsSchemaViolation()
    {
        string oversized = new('x', BrokerProtocolConstants.MaximumErrorMessageLength + 1);
        string json = FailureJson().Replace(
            "The operation could not be completed.",
            oversized,
            StringComparison.Ordinal);

        AssertResponseFailure(json, BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
    }

    [Fact]
    public void DeserializeResponse_RejectsAdministrativeCleanupRemovalIntent()
    {
        BrokerResponseEnvelope response = Success(
            BrokerCommand.RemoveLock,
            new RemoveLockResult(
                ProtocolTestData.TaskId,
                ProtocolTestData.RecoveryRecordId,
                LockRemovalIntent.Expiration,
                LockTaskStatus.Active,
                LockTaskStatus.Completed,
                ProtocolTestData.ServerTimeUtc,
                true,
                true,
                false));
        string json = System.Text.Encoding.UTF8.GetString(BrokerProtocolJson.SerializeResponse(response))
            .Replace("Expiration", "AdministrativeCleanup", StringComparison.Ordinal);

        AssertResponseFailure(json, BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
    }

    [Fact]
    public void TaskStatusError_HasExactlyThreePropertiesAndNoField()
    {
        BrokerResponseEnvelope response = Success(
            BrokerCommand.GetStatus,
            new GetStatusResult(
                GetStatusQueryType.ByTaskId,
                [new TaskStatusItem(
                    ProtocolTestData.TaskId,
                    @"C:\Data\Locked",
                    LockTaskStatus.RecoveryRequired,
                    ProtocolTestData.ServerTimeUtc,
                    ProtocolTestData.ServerTimeUtc.AddHours(1),
                    3_600_000,
                    0,
                    false,
                    true,
                    new TaskStatusError(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED, "Recovery is required.", false))]));

        using JsonDocument document = JsonDocument.Parse(BrokerProtocolJson.SerializeResponse(response));
        JsonElement error = document.RootElement.GetProperty("result").GetProperty("tasks")[0].GetProperty("error");

        Assert.Equal(["code", "message", "retryable"], error.EnumerateObject().Select(property => property.Name));
        Assert.False(error.TryGetProperty("field", out _));
    }

    private static BrokerResponseEnvelope Success(BrokerCommand command, IBrokerResult result) =>
        BrokerResponseEnvelope.Succeeded(
            ProtocolTestData.RequestId,
            command,
            ProtocolTestData.ServerTimeUtc,
            result);

    private static string FailureJson() => """
        {"protocolVersion":1,"requestId":"11111111-2222-3333-4444-555555555555","command":"ValidatePath","success":false,"serverTimeUtc":"2026-07-19T16:30:00.0000000Z","result":null,"error":{"code":"FSL_E_INTERNAL","message":"The operation could not be completed.","retryable":false,"field":null}}
        """;

    private static void AssertResponseFailure(string json, string code)
    {
        BrokerResponseParseResult result = ProtocolTestData.ParseResponse(json);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Response);
        Assert.True(
            result.Error!.Code == code,
            $"Expected {code}; received {result.Error.Code} at {result.Error.Field}.");
    }
}
