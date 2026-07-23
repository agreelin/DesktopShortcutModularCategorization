using System.Globalization;
using System.Text;
using System.Text.Json;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Core.Recovery;

public static class RecoveryReadinessJson
{
    private static readonly string[] FieldNames =
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
    ];

    private static readonly HashSet<string> Fields = new(FieldNames, StringComparer.Ordinal);

    public static byte[] Serialize(RecoveryReadinessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", snapshot.SchemaVersion);
            writer.WriteString("serviceName", snapshot.ServiceName);
            writer.WriteString("serviceInstanceId", FormatGuid(snapshot.ServiceInstanceId));
            writer.WriteNumber("sequence", snapshot.Sequence);
            writer.WriteString("state", StateName(snapshot.State));
            writer.WriteBoolean("recoveryBlocking", snapshot.RecoveryBlocking);
            writer.WriteString("scanStartedUtc", FormatTimestamp(snapshot.ScanStartedUtc));
            WriteNullableTimestamp(writer, "scanCompletedUtc", snapshot.ScanCompletedUtc);
            writer.WriteString("publishedUtc", FormatTimestamp(snapshot.PublishedUtc));
            writer.WriteString("validUntilUtc", FormatTimestamp(snapshot.ValidUntilUtc));
            writer.WriteNumber("remainingRecordCount", snapshot.RemainingRecordCount);
            if (snapshot.PrimaryErrorCode is null)
            {
                writer.WriteNull("primaryErrorCode");
            }
            else
            {
                writer.WriteString("primaryErrorCode", snapshot.PrimaryErrorCode);
            }

            writer.WriteEndObject();
        }

        byte[] bytes = stream.ToArray();
        if (bytes.Length is < 1 or > RecoveryReadinessPolicy.MaximumLength)
        {
            throw new ArgumentException("The recovery readiness snapshot is too large.", nameof(snapshot));
        }

        return bytes;
    }

    public static Result<RecoveryReadinessSnapshot> Deserialize(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length is < 1 or > RecoveryReadinessPolicy.MaximumLength
            || (utf8Json.Length >= 3
                && utf8Json.Span[0] == 0xEF
                && utf8Json.Span[1] == 0xBB
                && utf8Json.Span[2] == 0xBF))
        {
            return SchemaFailure();
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(utf8Json.Span);
            using JsonDocument document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SchemaFailure();
            }

            JsonProperty[] properties = root.EnumerateObject().ToArray();
            if (properties.Length != FieldNames.Length
                || properties.Any(property => !Fields.Contains(property.Name))
                || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
                    != FieldNames.Length)
            {
                return SchemaFailure();
            }

            if (!TryReadCanonicalInt32(root, "schemaVersion", out int schemaVersion))
            {
                return SchemaFailure();
            }

            if (schemaVersion != RecoveryReadinessPolicy.SchemaVersion)
            {
                return VersionFailure();
            }

            if (!TryReadExactString(root, "serviceName", out string? serviceName)
                || !string.Equals(serviceName, RecoveryReadinessPolicy.ServiceName, StringComparison.Ordinal)
                || !TryReadGuid(root, "serviceInstanceId", out Guid serviceInstanceId)
                || !TryReadCanonicalInt64(root, "sequence", out long sequence)
                || !TryReadState(root, "state", out RecoveryReadinessState state)
                || !TryReadBoolean(root, "recoveryBlocking", out bool recoveryBlocking)
                || !TryReadTimestamp(root, "scanStartedUtc", out DateTimeOffset scanStartedUtc)
                || !TryReadNullableTimestamp(root, "scanCompletedUtc", out DateTimeOffset? scanCompletedUtc)
                || !TryReadTimestamp(root, "publishedUtc", out DateTimeOffset publishedUtc)
                || !TryReadTimestamp(root, "validUntilUtc", out DateTimeOffset validUntilUtc)
                || !TryReadCanonicalInt32(root, "remainingRecordCount", out int remainingRecordCount)
                || !TryReadNullableErrorCode(root, "primaryErrorCode", out string? primaryErrorCode))
            {
                return SchemaFailure();
            }

            var snapshot = new RecoveryReadinessSnapshot(
                schemaVersion,
                serviceName!,
                serviceInstanceId,
                sequence,
                state,
                recoveryBlocking,
                scanStartedUtc,
                scanCompletedUtc,
                publishedUtc,
                validUntilUtc,
                remainingRecordCount,
                primaryErrorCode);
            string? error = RecoveryReadinessPolicy.Validate(snapshot, publishedUtc);
            return error is null
                ? Result<RecoveryReadinessSnapshot>.Success(snapshot)
                : Failure(error);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or OverflowException)
        {
            return SchemaFailure();
        }
    }

    private static bool TryReadCanonicalInt32(JsonElement root, string name, out int value)
    {
        value = default;
        JsonElement element = root.GetProperty(name);
        return element.ValueKind == JsonValueKind.Number
            && IsCanonicalInteger(element.GetRawText())
            && element.TryGetInt32(out value);
    }

    private static bool TryReadCanonicalInt64(JsonElement root, string name, out long value)
    {
        value = default;
        JsonElement element = root.GetProperty(name);
        return element.ValueKind == JsonValueKind.Number
            && IsCanonicalInteger(element.GetRawText())
            && element.TryGetInt64(out value);
    }

    private static bool IsCanonicalInteger(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        int start = text[0] == '-' ? 1 : 0;
        if (start == text.Length || (text.Length - start > 1 && text[start] == '0'))
        {
            return false;
        }

        return text.AsSpan(start).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool TryReadExactString(JsonElement root, string name, out string? value)
    {
        JsonElement element = root.GetProperty(name);
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool TryReadGuid(JsonElement root, string name, out Guid value)
    {
        value = default;
        return TryReadExactString(root, name, out string? text)
            && Guid.TryParseExact(text, BrokerProtocolConstants.GuidFormat, out value)
            && value != Guid.Empty
            && string.Equals(text, value.ToString("D"), StringComparison.Ordinal);
    }

    private static bool TryReadState(
        JsonElement root,
        string name,
        out RecoveryReadinessState state)
    {
        state = default;
        if (!TryReadExactString(root, name, out string? text))
        {
            return false;
        }

        state = text switch
        {
            "Starting" => RecoveryReadinessState.Starting,
            "Ready" => RecoveryReadinessState.Ready,
            "RecoveryBlocked" => RecoveryReadinessState.RecoveryBlocked,
            "Stopping" => RecoveryReadinessState.Stopping,
            _ => (RecoveryReadinessState)(-1),
        };
        return Enum.IsDefined(state);
    }

    private static bool TryReadBoolean(JsonElement root, string name, out bool value)
    {
        JsonElement element = root.GetProperty(name);
        value = default;
        if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadTimestamp(
        JsonElement root,
        string name,
        out DateTimeOffset value)
    {
        value = default;
        return TryReadExactString(root, name, out string? text)
            && DateTimeOffset.TryParseExact(
                text,
                BrokerProtocolConstants.UtcTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value)
            && string.Equals(text, FormatTimestamp(value), StringComparison.Ordinal);
    }

    private static bool TryReadNullableTimestamp(
        JsonElement root,
        string name,
        out DateTimeOffset? value)
    {
        JsonElement element = root.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (TryReadTimestamp(root, name, out DateTimeOffset timestamp))
        {
            value = timestamp;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryReadNullableErrorCode(
        JsonElement root,
        string name,
        out string? value)
    {
        JsonElement element = root.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null
            && value.Length <= RecoveryReadinessPolicy.MaximumPrimaryErrorCodeLength
            && BrokerProtocolValidation.IsErrorCode(value);
    }

    private static void WriteNullableTimestamp(
        Utf8JsonWriter writer,
        string name,
        DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, FormatTimestamp(value.Value));
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) => value
        .ToUniversalTime()
        .ToString(BrokerProtocolConstants.UtcTimestampFormat, CultureInfo.InvariantCulture);

    private static string FormatGuid(Guid value) => value.ToString("D");

    private static string StateName(RecoveryReadinessState state) => state switch
    {
        RecoveryReadinessState.Starting => "Starting",
        RecoveryReadinessState.Ready => "Ready",
        RecoveryReadinessState.RecoveryBlocked => "RecoveryBlocked",
        RecoveryReadinessState.Stopping => "Stopping",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static Result<RecoveryReadinessSnapshot> SchemaFailure() => Failure(
        BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID);

    private static Result<RecoveryReadinessSnapshot> VersionFailure() => Failure(
        BrokerErrorCodes.FSL_E_RECOVERY_READINESS_VERSION_UNSUPPORTED);

    private static Result<RecoveryReadinessSnapshot> Failure(string code) =>
        Result<RecoveryReadinessSnapshot>.Failure(new Error(
            code,
            code,
            ErrorCategory.UnrecoverableError));
}
