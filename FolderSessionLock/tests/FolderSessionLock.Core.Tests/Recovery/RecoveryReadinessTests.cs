using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Core.Tests.Recovery;

public sealed class RecoveryReadinessTests
{
    private static readonly DateTimeOffset Published =
        new(2026, 7, 22, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Json_RoundTripsTheExactTwelveFieldsWithoutBom()
    {
        RecoveryReadinessSnapshot snapshot = Ready();

        byte[] bytes = RecoveryReadinessJson.Serialize(snapshot);
        var result = RecoveryReadinessJson.Deserialize(bytes);

        Assert.True(result.IsSuccess);
        Assert.Equal(snapshot, result.Value);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.Equal(
            [
                "schemaVersion",
                "serviceName",
                "serviceInstanceId",
                "sequence",
                "state",
                "recoveryBlocking",
                "scanStartedUtc",
                "scanCompletedUtc",
                "publishedUtc",
                "validUntilUtc",
                "remainingRecordCount",
                "primaryErrorCode",
            ],
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [MemberData(nameof(ValidStates))]
    public void Policy_AcceptsEachExactStateMatrix(RecoveryReadinessSnapshot snapshot)
    {
        Assert.Null(RecoveryReadinessPolicy.Validate(snapshot, Published.AddSeconds(1)));
    }

    [Theory]
    [MemberData(nameof(InvalidStates))]
    public void Policy_RejectsInvalidStateMatrices(RecoveryReadinessSnapshot snapshot)
    {
        Assert.Equal(
            BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID,
            RecoveryReadinessPolicy.Validate(snapshot, Published.AddSeconds(1)));
    }

    [Fact]
    public void Policy_RejectsStaleFutureAndNonIncreasingSequence()
    {
        RecoveryReadinessSnapshot ready = Ready();

        Assert.Equal(
            BrokerErrorCodes.FSL_E_RECOVERY_READINESS_STALE,
            RecoveryReadinessPolicy.Validate(ready, ready.ValidUntilUtc.AddTicks(1)));
        Assert.Equal(
            BrokerErrorCodes.FSL_E_RECOVERY_READINESS_STALE,
            RecoveryReadinessPolicy.Validate(
                ready with
                {
                    PublishedUtc = Published.AddSeconds(6),
                    ValidUntilUtc = Published.AddSeconds(36),
                },
                Published));
        Assert.Equal(
            BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID,
            RecoveryReadinessPolicy.Validate(
                ready with { Sequence = 1 },
                Published,
                ready with { Sequence = 1 }));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("[]")]
    [InlineData("{} {}")]
    public void Json_RejectsMissingDuplicateAndInvalidRootShapes(string json)
    {
        var result = RecoveryReadinessJson.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID, result.Error!.Code);
    }

    [Fact]
    public void Json_RejectsExtraFieldUppercaseGuidExponentAndNonCanonicalTimestamp()
    {
        byte[] valid = RecoveryReadinessJson.Serialize(Ready());

        AssertSchemaInvalid(Mutate(valid, root => root["extra"] = true));
        AssertSchemaInvalid(Mutate(valid, root => root["serviceInstanceId"] =
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee".ToUpperInvariant()));
        AssertSchemaInvalid(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(valid).Replace("\"sequence\":1", "\"sequence\":1e0", StringComparison.Ordinal)));
        AssertSchemaInvalid(Mutate(valid, root => root["publishedUtc"] = "2026-07-22T01:00:00Z"));
    }

    [Fact]
    public void ConsentBrokerExitCodes_AreTheExactClosedSet()
    {
        Assert.Equal(
            [0, 2, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29],
            Enum.GetValues<ConsentBrokerExitCode>().Select(value => (int)value));
    }

    public static TheoryData<RecoveryReadinessSnapshot> ValidStates() => new()
    {
        Starting(),
        Ready(),
        Ready() with
        {
            State = RecoveryReadinessState.RecoveryBlocked,
            RecoveryBlocking = true,
            RemainingRecordCount = 0,
            PrimaryErrorCode = BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_INVALID,
        },
        Starting() with
        {
            State = RecoveryReadinessState.Stopping,
            PrimaryErrorCode = BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_INVALID,
        },
    };

    public static TheoryData<RecoveryReadinessSnapshot> InvalidStates() => new()
    {
        Starting() with { RecoveryBlocking = false },
        Starting() with { ScanCompletedUtc = Published },
        Starting() with { RemainingRecordCount = 0 },
        Ready() with { RecoveryBlocking = true },
        Ready() with { ScanCompletedUtc = null },
        Ready() with { RemainingRecordCount = 1 },
        Ready() with { PrimaryErrorCode = BrokerErrorCodes.FSL_E_INTERNAL },
        Ready() with { State = RecoveryReadinessState.RecoveryBlocked },
        Starting() with { State = RecoveryReadinessState.Stopping, RecoveryBlocking = false },
    };

    private static RecoveryReadinessSnapshot Starting() => new(
        1,
        "FolderSessionLockRecovery",
        Guid.Parse("11111111-2222-4333-8444-555555555555"),
        1,
        RecoveryReadinessState.Starting,
        true,
        Published,
        null,
        Published,
        Published.AddSeconds(30),
        -1,
        null);

    private static RecoveryReadinessSnapshot Ready() => Starting() with
    {
        State = RecoveryReadinessState.Ready,
        RecoveryBlocking = false,
        ScanCompletedUtc = Published,
        RemainingRecordCount = 0,
    };

    private static byte[] Mutate(byte[] source, Action<JsonObject> mutation)
    {
        JsonObject root = JsonNode.Parse(source)!.AsObject();
        mutation(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static void AssertSchemaInvalid(byte[] bytes)
    {
        var result = RecoveryReadinessJson.Deserialize(bytes);
        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID, result.Error!.Code);
    }
}
