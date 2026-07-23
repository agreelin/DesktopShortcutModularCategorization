namespace FolderSessionLock.Protocol.Tests;

public sealed class BrokerProtocolJsonRequestTests
{
    public static TheoryData<string> InvalidCreateLockTaskIds => new()
    {
        "A0B1C2D3-E4F5-4678-9123-ABCDEFABCDEF",
        "{a0b1c2d3-e4f5-4678-9123-abcdefabcdef}",
        "00000000-0000-0000-0000-000000000000",
    };

    public static TheoryData<string> InvalidDurations => new()
    {
        "1.0",
        "1e3",
        "\"60000\"",
        "9223372036854775808",
    };

    [Fact]
    public void DeserializeRequest_ParsesTheExactSchemaForAllFourCommands()
    {
        BrokerRequestParseResult validatePath = ProtocolTestData.ParseRequest(
            ProtocolTestData.Request("ValidatePath", "{\"path\":\"C:\\\\Data\\\\Locked\"}"));
        BrokerRequestParseResult createLock = ProtocolTestData.ParseRequest(
            ProtocolTestData.Request(
                "CreateLock",
                $$"""{"taskId":"{{ProtocolTestData.TaskId:D}}","path":"C:\\Data\\Locked","durationMilliseconds":3600000}"""));
        BrokerRequestParseResult removeLock = ProtocolTestData.ParseRequest(
            ProtocolTestData.Request(
                "RemoveLock",
                $$"""{"taskId":"{{ProtocolTestData.TaskId:D}}","recoveryRecordId":"{{ProtocolTestData.RecoveryRecordId:D}}"}"""));
        BrokerRequestParseResult getStatus = ProtocolTestData.ParseRequest(
            ProtocolTestData.Request(
                "GetStatus",
                $$"""{"queryType":"ByTaskId","taskId":"{{ProtocolTestData.TaskId:D}}"}"""));

        Assert.IsType<ValidatePathRequest>(validatePath.Request!.Payload);
        Assert.IsType<CreateLockRequest>(createLock.Request!.Payload);
        Assert.IsType<RemoveLockRequest>(removeLock.Request!.Payload);
        Assert.IsType<GetStatusRequest>(getStatus.Request!.Payload);
        Assert.All(
            new[] { validatePath, createLock, removeLock, getStatus },
            result => Assert.True(result.IsSuccess));
    }

    [Fact]
    public void DeserializeRequest_RejectsDuplicatePropertyAsMalformed()
    {
        string json = $$"""
            {
              "protocolVersion": 1,
              "requestId": "{{ProtocolTestData.RequestId:D}}",
              "command": "ValidatePath",
              "clientSessionId": 1,
              "sentAtUtc": "2026-07-19T16:30:00.0000000Z",
              "payload": {"path":"C:\\Data\\Locked","path":"C:\\Other"}
            }
            """;

        AssertFailure(ProtocolTestData.ParseRequest(json), BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
    }

    [Fact]
    public void DeserializeRequest_RejectsExtraMissingAndNullFieldsAsSchemaViolations()
    {
        string extra = ProtocolTestData.Request(
            "ValidatePath",
            "{\"path\":\"C:\\\\Data\\\\Locked\",\"unexpected\":true}");
        string missing = ProtocolTestData.Request("ValidatePath", "{}");
        string nullValue = ProtocolTestData.Request("ValidatePath", "{\"path\":null}");

        AssertFailure(ProtocolTestData.ParseRequest(extra), BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
        AssertFailure(ProtocolTestData.ParseRequest(missing), BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
        AssertFailure(ProtocolTestData.ParseRequest(nullValue), BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
    }

    [Theory]
    [MemberData(nameof(InvalidCreateLockTaskIds))]
    public void DeserializeRequest_RejectsNonProtocolGuidFormatsAsMalformed(string taskId)
    {
        string payload = $$"""{"taskId":"{{taskId}}","path":"C:\\Data\\Locked","durationMilliseconds":3600000}""";

        AssertFailure(
            ProtocolTestData.ParseRequest(ProtocolTestData.Request("CreateLock", payload)),
            BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
    }

    [Theory]
    [InlineData("2026-07-19T16:30:00Z")]
    [InlineData("2026-07-19T16:30:00.0000000+00:00")]
    [InlineData("2026-07-19T16:30:00.0000000z")]
    public void DeserializeRequest_RejectsNonProtocolUtcFormatsAsMalformed(string timestamp)
    {
        string json = ProtocolTestData.Request("ValidatePath", "{\"path\":\"C:\\\\Data\\\\Locked\"}")
            .Replace("2026-07-19T16:30:00.0000000Z", timestamp, StringComparison.Ordinal);

        BrokerRequestParseResult result = ProtocolTestData.ParseRequest(json);

        AssertFailure(result, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
        Assert.Null(result.FailureResponse!.RequestId);
        Assert.Null(result.FailureResponse.Command);
    }

    [Theory]
    [MemberData(nameof(InvalidDurations))]
    public void DeserializeRequest_RejectsNonIntegerOrOverflowDurationAsMalformed(string durationToken)
    {
        string payload = $$"""{"taskId":"{{ProtocolTestData.TaskId:D}}","path":"C:\\Data\\Locked","durationMilliseconds":{{durationToken}}}""";

        AssertFailure(
            ProtocolTestData.ParseRequest(ProtocolTestData.Request("CreateLock", payload)),
            BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
    }

    [Fact]
    public void DeserializeRequest_RejectsUnknownEnumAndInvalidGetStatusCombinationsAsSchemaViolations()
    {
        string unknown = ProtocolTestData.Request(
            "GetStatus",
            "{\"queryType\":\"AllTasks\",\"taskId\":null}");
        string byTaskIdWithoutTask = ProtocolTestData.Request(
            "GetStatus",
            "{\"queryType\":\"ByTaskId\",\"taskId\":null}");
        string currentSessionWithTask = ProtocolTestData.Request(
            "GetStatus",
            $$"""{"queryType":"CurrentSession","taskId":"{{ProtocolTestData.TaskId:D}}"}""");

        AssertFailure(ProtocolTestData.ParseRequest(unknown), BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
        AssertFailure(ProtocolTestData.ParseRequest(byTaskIdWithoutTask), BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
        AssertFailure(ProtocolTestData.ParseRequest(currentSessionWithTask), BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION);
        Assert.True(ProtocolTestData.ParseRequest(ProtocolTestData.Request(
            "GetStatus",
            "{\"queryType\":\"CurrentSession\",\"taskId\":null}")).IsSuccess);
    }

    [Theory]
    [InlineData("acl")]
    [InlineData("sid")]
    [InlineData("removalIntent")]
    [InlineData("intent")]
    public void DeserializeRequest_RejectsClientControlledSecurityFields(string field)
    {
        string sensitive = @"S-1-5-21-1000 C:\ProgramData\FolderSessionLock secret";
        string payload = $$"""{"taskId":"{{ProtocolTestData.TaskId:D}}","recoveryRecordId":"{{ProtocolTestData.RecoveryRecordId:D}}","{{field}}":{{ProtocolTestData.JsonString(sensitive)}}}""";

        BrokerRequestParseResult result = ProtocolTestData.ParseRequest(
            ProtocolTestData.Request("RemoveLock", payload));

        AssertFailure(result, BrokerErrorCodes.FSL_E_FORBIDDEN_INPUT);
        Assert.Equal($"payload.{field}", result.FailureResponse!.Error!.Field);
        Assert.DoesNotContain(sensitive, result.FailureResponse.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeRequest_PrioritizesMalformedTypeBeforeForbiddenField()
    {
        string payload = $$"""{"taskId":"{{ProtocolTestData.TaskId:D}}","path":"C:\\Data\\Locked","durationMilliseconds":"3600000","acl":"secret"}""";

        AssertFailure(
            ProtocolTestData.ParseRequest(ProtocolTestData.Request("CreateLock", payload)),
            BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
    }

    [Fact]
    public void DeserializeRequest_ReturnsNullIdentifiersWhenRequestIdOrCommandCannotBeParsed()
    {
        string invalidRequestId = ProtocolTestData.Request(
                "ValidatePath",
                "{\"path\":\"C:\\\\Data\\\\Locked\"}")
            .Replace(ProtocolTestData.RequestId.ToString("D"), "INVALID", StringComparison.Ordinal);
        string invalidCommandType = ProtocolTestData.Request(
                "ValidatePath",
                "{\"path\":\"C:\\\\Data\\\\Locked\"}")
            .Replace("\"command\": \"ValidatePath\"", "\"command\": 1", StringComparison.Ordinal);

        BrokerRequestParseResult requestIdResult = ProtocolTestData.ParseRequest(invalidRequestId);
        BrokerRequestParseResult commandResult = ProtocolTestData.ParseRequest(invalidCommandType);

        AssertFailure(requestIdResult, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
        Assert.Null(requestIdResult.FailureResponse!.RequestId);
        Assert.Null(requestIdResult.FailureResponse.Command);
        AssertFailure(commandResult, BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE);
        Assert.Null(commandResult.FailureResponse!.RequestId);
        Assert.Null(commandResult.FailureResponse.Command);
    }

    [Fact]
    public void DeserializeRequest_ReturnsUnknownCommandForExactUnsupportedText()
    {
        BrokerRequestParseResult result = ProtocolTestData.ParseRequest(
            ProtocolTestData.Request("validatelock", "{}"));

        AssertFailure(result, BrokerErrorCodes.FSL_E_UNKNOWN_COMMAND);
        Assert.Equal(ProtocolTestData.RequestId, result.FailureResponse!.RequestId);
        Assert.Null(result.FailureResponse.Command);
    }

    [Fact]
    public void DeserializeRequest_PrioritizesUnsupportedVersionBeforeUnknownCommand()
    {
        BrokerRequestParseResult result = ProtocolTestData.ParseRequest(
            ProtocolTestData.Request("NotACommand", "{}", "2"));

        AssertFailure(result, BrokerErrorCodes.FSL_E_PROTOCOL_VERSION_UNSUPPORTED);
    }

    private static void AssertFailure(BrokerRequestParseResult result, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Request);
        Assert.Equal(code, result.FailureResponse!.Error!.Code);
        Assert.False(result.FailureResponse.Success);
        Assert.Null(result.FailureResponse.Result);
    }
}
