using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Nodes;
using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class RecoveryRecordJsonTests
{
    private static readonly string[] FieldNames =
    [
        "schemaVersion",
        "writerVersion",
        "recordId",
        "taskId",
        "state",
        "normalizedPath",
        "volumeSerialNumber",
        "fileIdHigh",
        "fileIdLow",
        "accountSid",
        "logonSid",
        "windowsSessionId",
        "aceType",
        "accessMask",
        "inheritanceFlags",
        "propagationFlags",
        "aceFingerprintSha256",
        "baselineDaclSha256",
        "postApplyDaclSha256",
        "createdUtc",
        "expiresUtc",
        "lastUpdatedUtc",
        "cleanupAttemptCount",
        "lastErrorCode",
        "lastErrorMessage",
    ];

    [Fact]
    public void SerializeAndDeserialize_PreservesExactTwentyFiveFieldContract()
    {
        RecoveryRecord record = RecoveryTestData.Prepared();

        byte[] json = RecoveryRecordJson.Serialize(record);
        RecoveryRecordReadResult result = RecoveryRecordJson.Deserialize(json);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(record, result.Record);
        Assert.Equal(25, document.RootElement.EnumerateObject().Count());
        Assert.Equal(
            [
                "schemaVersion",
                "writerVersion",
                "recordId",
                "taskId",
                "state",
                "normalizedPath",
                "volumeSerialNumber",
                "fileIdHigh",
                "fileIdLow",
                "accountSid",
                "logonSid",
                "windowsSessionId",
                "aceType",
                "accessMask",
                "inheritanceFlags",
                "propagationFlags",
                "aceFingerprintSha256",
                "baselineDaclSha256",
                "postApplyDaclSha256",
                "createdUtc",
                "expiresUtc",
                "lastUpdatedUtc",
                "cleanupAttemptCount",
                "lastErrorCode",
                "lastErrorMessage",
            ],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("postApplyDaclSha256").ValueKind);
    }

    [Fact]
    public void Deserialize_RejectsDuplicateMissingAndExtraFields()
    {
        string valid = RecoveryTestData.Json(RecoveryTestData.Prepared());
        string duplicate = valid.Replace(
            "\"schemaVersion\":1,",
            "\"schemaVersion\":1,\"schemaVersion\":1,",
            StringComparison.Ordinal);
        JsonObject missing = JsonNode.Parse(valid)!.AsObject();
        missing.Remove("taskId");
        JsonObject extra = JsonNode.Parse(valid)!.AsObject();
        extra["unexpected"] = true;

        AssertCode(duplicate, "FSL_E_RECOVERY_PAYLOAD_MALFORMED");
        AssertCode(missing.ToJsonString(), "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID");
        AssertCode(extra.ToJsonString(), "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID");
    }

    public static IEnumerable<object[]> EveryFieldName() =>
        FieldNames.Select(field => new object[] { field });

    public static IEnumerable<object[]> EveryNonNullableFieldName() =>
        FieldNames
            .Except(["postApplyDaclSha256", "lastErrorCode", "lastErrorMessage"])
            .Select(field => new object[] { field });

    public static IEnumerable<object[]> EveryFieldWithWrongType()
    {
        var numericFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "windowsSessionId",
            "accessMask",
            "inheritanceFlags",
            "propagationFlags",
            "cleanupAttemptCount",
        };

        foreach (string field in FieldNames)
        {
            yield return [field, numericFields.Contains(field) ? "\"wrong\"" : "false"];
        }
    }

    [Theory]
    [MemberData(nameof(EveryFieldName))]
    public void Deserialize_RejectsEachMissingField(string field)
    {
        JsonObject json = PreparedJson();
        json.Remove(field);

        AssertCode(json.ToJsonString(), "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID");
    }

    [Theory]
    [MemberData(nameof(EveryNonNullableFieldName))]
    public void Deserialize_RejectsNullForEveryNonNullableField(string field)
    {
        JsonObject json = PreparedJson();
        json[field] = null;

        AssertCode(json.ToJsonString(), "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID");
    }

    [Theory]
    [MemberData(nameof(EveryFieldWithWrongType))]
    public void Deserialize_RejectsWrongTypeForEveryField(string field, string rawValue)
    {
        JsonObject json = PreparedJson();
        json[field] = JsonNode.Parse(rawValue);

        AssertCode(json.ToJsonString(), "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID");
    }

    [Theory]
    [InlineData("volumeSerialNumber", "0123456789ABCDEf")]
    [InlineData("fileIdHigh", "01")]
    [InlineData("recordId", "12345678-1234-4234-0234-123456789abc")]
    [InlineData("createdUtc", "2026-07-19T16:30:00Z")]
    [InlineData("logonSid", "S-1-5-21-1-2-3-4")]
    [InlineData("aceFingerprintSha256", "366092CAEF8B4CCD9A05728CC017B2B155A9F8AA74358E6DF901E0554A8239F7")]
    public void Deserialize_RejectsNonCanonicalFields(string field, string value)
    {
        JsonObject json = JsonNode.Parse(RecoveryTestData.Json(RecoveryTestData.Prepared()))!.AsObject();
        json[field] = value;

        AssertCode(json.ToJsonString(), "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID");
    }

    public static IEnumerable<object[]> InvalidSchemaValues()
    {
        yield return ["writerVersion", "\"1.O\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["recordId", "\"\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["recordId", "\"12345678-1234-4234-8234-123456789ABC\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["taskId", "\"00000000-0000-0000-0000-000000000000\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["taskId", "\"AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["state", "\"prepared\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["state", "\"Unknown\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["normalizedPath", "\"\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["volumeSerialNumber", "\"123456789abcdef\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["volumeSerialNumber", "\"0123456789abcdef0\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["volumeSerialNumber", "\"0x123456789abcdef\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["volumeSerialNumber", "\"0123456789ABCDEF\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["fileIdHigh", "\"01\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["fileIdHigh", "\"-1\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["fileIdHigh", "\"18446744073709551616\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["fileIdLow", "\"01\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["fileIdLow", "\"-1\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["fileIdLow", "\"18446744073709551616\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["accountSid", "\"not-a-sid\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["accountSid", "\"s-1-5-18\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["logonSid", "\"S-1-5-5-1\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["logonSid", "\"S-1-5-5-1-2-3\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["windowsSessionId", "-1", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["windowsSessionId", "4294967296", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["windowsSessionId", "1.0", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["windowsSessionId", "1e0", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["aceType", "\"Allow\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["aceType", "\"deny\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["accessMask", "0", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["accessMask", "-1", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["accessMask", "4294967296", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["inheritanceFlags", "4", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["inheritanceFlags", "-1", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["propagationFlags", "4", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["propagationFlags", "-1", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["aceFingerprintSha256", $"\"{new string('a', 63)}\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["aceFingerprintSha256", $"\"{new string('0', 64)}\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["baselineDaclSha256", $"\"{new string('A', 64)}\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["baselineDaclSha256", $"\"{new string('0', 64)}\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["postApplyDaclSha256", $"\"{new string('a', 65)}\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["postApplyDaclSha256", $"\"{new string('0', 64)}\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["createdUtc", "\"2026-07-19T16:30:00Z\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["createdUtc", "\"2026-07-19T16:30:00.0000000+00:00\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["expiresUtc", "\"2026-07-19T18:30:00.0000000+00:00\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["lastUpdatedUtc", "\"2026-07-19T16:30:00.000000Z\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["cleanupAttemptCount", "-1", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["cleanupAttemptCount", "1000001", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["lastErrorCode", "\"fsl_e_failed\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["lastErrorCode", $"\"FSL_E_{new string('A', 123)}\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["lastErrorMessage", "\"\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["lastErrorMessage", $"\"{new string('x', 257)}\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
    }

    [Theory]
    [MemberData(nameof(InvalidSchemaValues))]
    public void Deserialize_RejectsExactSchemaBoundsCanonicalAndCaseViolations(
        string field,
        string rawValue,
        string expectedCode)
    {
        JsonObject json = PreparedJson();
        json[field] = JsonNode.Parse(rawValue);

        AssertCode(json.ToJsonString(), expectedCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Deserialize_RejectsUnsupportedSchemaVersion(int schemaVersion)
    {
        JsonObject json = PreparedJson();
        json["schemaVersion"] = schemaVersion;

        AssertCode(json.ToJsonString(), "FSL_E_RECOVERY_PAYLOAD_VERSION_UNSUPPORTED");
    }

    [Fact]
    public void SerializeAndDeserialize_AcceptsNumericAndFlagBoundaries()
    {
        RecoveryRecord record = RecoveryTestData.Prepared() with
        {
            VolumeSerialNumber = ulong.MaxValue,
            FileIdHigh = ulong.MaxValue,
            FileIdLow = 0,
            WindowsSessionId = uint.MaxValue,
            AccessMask = uint.MaxValue,
            InheritanceFlags = (InheritanceFlags)3,
            PropagationFlags = (PropagationFlags)3,
        };

        RecoveryRecordReadResult result = RecoveryRecordJson.Deserialize(
            RecoveryRecordJson.Serialize(record));

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(record, result.Record);
    }

    [Fact]
    public void StateMatrix_AcceptsFourExactStatesAndCleanupCountMaximum()
    {
        RecoveryRecord prepared = RecoveryTestData.Prepared();
        RecoveryRecord applied = RecoveryTestData.Applied();
        RecoveryRecord pending = applied with
        {
            State = RecoveryRecordState.CleanupPending,
            CleanupAttemptCount = 1,
            LastUpdatedUtc = applied.ExpiresUtc,
        };
        RecoveryRecord failed = pending with
        {
            State = RecoveryRecordState.CleanupFailed,
            CleanupAttemptCount = 1_000_000,
            LastErrorCode = "FSL_E_ACL_REMOVE_FAILED",
            LastErrorMessage = "FSL_E_ACL_REMOVE_FAILED",
        };

        Assert.All(
            new[] { prepared, applied, pending, failed },
            record => Assert.True(RecoveryRecordJson.Deserialize(RecoveryRecordJson.Serialize(record)).IsSuccess));
    }

    public static IEnumerable<object[]> InvalidStateCombinations()
    {
        yield return ["Prepared", "postApplyDaclSha256", $"\"{RecoveryTestData.PostApplyHash}\"", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["Prepared", "lastErrorCode", "\"FSL_E_FAILED\"", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["Prepared", "lastErrorMessage", "\"failed\"", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["Prepared", "cleanupAttemptCount", "1", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["Prepared", "lastUpdatedUtc", "\"2026-07-19T16:30:00.0000001Z\"", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["Applied", "postApplyDaclSha256", "null", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["Applied", "lastErrorCode", "\"FSL_E_FAILED\"", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["Applied", "lastErrorMessage", "\"failed\"", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["Applied", "cleanupAttemptCount", "1", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["CleanupPending", "postApplyDaclSha256", "null", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["CleanupPending", "lastErrorCode", "\"FSL_E_FAILED\"", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["CleanupPending", "lastErrorMessage", "\"failed\"", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["CleanupPending", "cleanupAttemptCount", "0", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["CleanupFailed", "postApplyDaclSha256", "null", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["CleanupFailed", "lastErrorCode", "null", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["CleanupFailed", "lastErrorMessage", "null", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
        yield return ["CleanupFailed", "lastErrorCode", "\"\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["CleanupFailed", "lastErrorMessage", "\"\"", "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID"];
        yield return ["CleanupFailed", "cleanupAttemptCount", "0", "FSL_E_RECOVERY_PAYLOAD_STATE_INVALID"];
    }

    [Theory]
    [MemberData(nameof(InvalidStateCombinations))]
    public void StateMatrix_RejectsEveryRequiredCounterexample(
        string state,
        string field,
        string rawValue,
        string expectedCode)
    {
        JsonObject json = StateJson(state);
        json[field] = JsonNode.Parse(rawValue);

        AssertCode(json.ToJsonString(), expectedCode);
    }

    [Fact]
    public void Deserialize_RejectsCrossFieldTimeBounds()
    {
        JsonObject expiresAtCreated = PreparedJson();
        expiresAtCreated["expiresUtc"] = expiresAtCreated["createdUtc"]!.GetValue<string>();
        JsonObject updatedBeforeCreated = PreparedJson();
        updatedBeforeCreated["lastUpdatedUtc"] = "2026-07-19T16:29:59.9999999Z";

        AssertCode(expiresAtCreated.ToJsonString(), "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID");
        AssertCode(updatedBeforeCreated.ToJsonString(), "FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID");
    }

    private static JsonObject PreparedJson() =>
        JsonNode.Parse(RecoveryTestData.Json(RecoveryTestData.Prepared()))!.AsObject();

    private static JsonObject StateJson(string state)
    {
        RecoveryRecord applied = RecoveryTestData.Applied();
        RecoveryRecord record = state switch
        {
            "Prepared" => RecoveryTestData.Prepared(),
            "Applied" => applied,
            "CleanupPending" => applied with
            {
                State = RecoveryRecordState.CleanupPending,
                CleanupAttemptCount = 1,
            },
            "CleanupFailed" => applied with
            {
                State = RecoveryRecordState.CleanupFailed,
                CleanupAttemptCount = 1,
                LastErrorCode = "FSL_E_FAILED",
                LastErrorMessage = "failed",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        return JsonNode.Parse(RecoveryTestData.Json(record))!.AsObject();
    }

    private static void AssertCode(string json, string code)
    {
        RecoveryRecordReadResult result = RecoveryRecordJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
    }
}

internal static class RecoveryTestData
{
    internal const string AceHash = "366092caef8b4ccd9a05728cc017b2b155a9f8aa74358e6df901e0554a8239f7";
    internal const string BaselineHash = "62fffcf46d188397e84da5b800129f54cacc87fe86ef9ca1f9eac9c6eef2db17";
    internal const string PostApplyHash = "0bd878690d59d8de240e84199560b65db09c2f473dffc717aabb75642566f026";

    internal static RecoveryRecord Prepared() => new(
        1,
        "1.0",
        Guid.Parse("12345678-1234-4234-8234-123456789abc"),
        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        RecoveryRecordState.Prepared,
        @"C:\Data\Locked",
        0x0123456789abcdef,
        1084818905618843912,
        506097522914230528,
        "S-1-5-21-1000-1001-1002-1003",
        "S-1-5-5-1-2",
        1,
        AccessControlType.Deny,
        0x000101ff,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AceHash,
        BaselineHash,
        null,
        new DateTimeOffset(2026, 7, 19, 16, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 19, 18, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 19, 16, 30, 0, TimeSpan.Zero),
        0,
        null,
        null);

    internal static RecoveryRecord Applied() => Prepared() with
    {
        State = RecoveryRecordState.Applied,
        PostApplyDaclSha256 = PostApplyHash,
        LastUpdatedUtc = Prepared().CreatedUtc.AddSeconds(1),
    };

    internal static string Json(RecoveryRecord record) =>
        System.Text.Encoding.UTF8.GetString(RecoveryRecordJson.Serialize(record));

    internal static FileRecoveryRecordStore CreateStore(
        string directory,
        IFileRecoveryRecordStoreTestHook? testHook = null,
        IRecoveryStoreFilePlatform? filePlatform = null,
        IRecoveryRecordFileSecurity? fileSecurity = null,
        IRecoveryStoreWriteSafetyState? writeSafetyState = null)
    {
        IRecoveryStoreFilePlatform platform = filePlatform ?? new WindowsRecoveryStoreFilePlatform();
        return FileRecoveryRecordStore.CreateForTest(
            directory,
            new TrustedProtectedPathVerifier(),
            fileSecurity ?? new TrustedRecoveryRecordFileSecurity(platform),
            platform,
            RecoveryStoreMutex.CreateForTest(MutexName(directory)),
            writeSafetyState ?? new RecoveryStoreWriteSafetyState(),
            testHook: testHook);
    }

    internal static RecoveryDirectoryEnumerator CreateEnumerator(string directory)
    {
        var platform = new WindowsRecoveryStoreFilePlatform();
        return new RecoveryDirectoryEnumerator(
            directory,
            new TrustedRecoveryRecordFileSecurity(platform),
            platform);
    }

    private static string MutexName(string directory)
    {
        string root = Path.Combine(Path.GetTempPath(), "FolderSessionLock.Tests");
        string relative = Path.GetRelativePath(root, Path.GetFullPath(directory));
        string id = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries)[0];
        return $"FolderSessionLock.Tests.RecoveryStore.{id}";
    }

    private sealed class TrustedProtectedPathVerifier : IProtectedPathSecurityVerifier
    {
        public ValueTask<ProtectedPathSecurityCheckResult> VerifyAsync(
            ProtectedPathSecurityCheckRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProtectedPathSecurityCheckResult(true, null));
    }

    private sealed class TrustedRecoveryRecordFileSecurity(
        IRecoveryStoreFilePlatform platform) : IRecoveryRecordFileSecurity
    {
        public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> ApplyAndVerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken) => VerifyAsync(
                fileHandle,
                fileKind,
                cancellationToken);

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
}
