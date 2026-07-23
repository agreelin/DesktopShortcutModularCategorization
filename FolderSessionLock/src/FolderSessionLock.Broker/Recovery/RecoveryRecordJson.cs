using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Recovery;

internal static class RecoveryRecordJson
{
    internal const int MaximumPlaintextLength = 131072;
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly HashSet<string> Fields =
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

    internal static byte[] Serialize(RecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", record.SchemaVersion);
            writer.WriteString("writerVersion", record.WriterVersion);
            writer.WriteString("recordId", FormatGuid(record.RecordId));
            writer.WriteString("taskId", FormatGuid(record.TaskId));
            writer.WriteString("state", record.State.ToString());
            writer.WriteString("normalizedPath", record.NormalizedPath);
            writer.WriteString("volumeSerialNumber", record.VolumeSerialNumber.ToString("x16", CultureInfo.InvariantCulture));
            writer.WriteString("fileIdHigh", record.FileIdHigh.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("fileIdLow", record.FileIdLow.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("accountSid", record.AccountSid);
            writer.WriteString("logonSid", record.LogonSid);
            writer.WriteNumber("windowsSessionId", record.WindowsSessionId);
            writer.WriteString("aceType", record.AceType.ToString());
            writer.WriteNumber("accessMask", record.AccessMask);
            writer.WriteNumber("inheritanceFlags", (int)record.InheritanceFlags);
            writer.WriteNumber("propagationFlags", (int)record.PropagationFlags);
            writer.WriteString("aceFingerprintSha256", record.AceFingerprintSha256);
            writer.WriteString("baselineDaclSha256", record.BaselineDaclSha256);
            WriteNullableString(writer, "postApplyDaclSha256", record.PostApplyDaclSha256);
            writer.WriteString("createdUtc", FormatDate(record.CreatedUtc));
            writer.WriteString("expiresUtc", FormatDate(record.ExpiresUtc));
            writer.WriteString("lastUpdatedUtc", FormatDate(record.LastUpdatedUtc));
            writer.WriteNumber("cleanupAttemptCount", record.CleanupAttemptCount);
            WriteNullableString(writer, "lastErrorCode", record.LastErrorCode);
            WriteNullableString(writer, "lastErrorMessage", record.LastErrorMessage);
            writer.WriteEndObject();
        }

        byte[] json = stream.ToArray();
        if (json.Length > MaximumPlaintextLength)
        {
            throw new ArgumentException("The recovery payload exceeds the fixed plaintext limit.", nameof(record));
        }

        RecoveryRecordReadResult validation = Deserialize(json);
        if (!validation.IsSuccess || validation.Record != record)
        {
            throw new ArgumentException("The recovery record does not satisfy the v1 payload contract.", nameof(record));
        }

        return json;
    }

    internal static RecoveryRecordReadResult Deserialize(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaximumPlaintextLength)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.PayloadTooLarge);
        }

        if (json.Length >= 3 && json[0] == 0xef && json[1] == 0xbb && json[2] == 0xbf)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.PayloadMalformed);
        }

        byte[] bytes = json.ToArray();
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.CurrentDepth == 1
                    && !names.Add(reader.GetString()!))
                {
                    return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.PayloadMalformed);
                }
            }

            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SchemaFailure();
            }

            string[] actualFields = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (actualFields.Length != Fields.Count || actualFields.Any(field => !Fields.Contains(field)))
            {
                return SchemaFailure();
            }

            return Parse(root);
        }
        catch (JsonException)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.PayloadMalformed);
        }
        catch (DecoderFallbackException)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.PayloadMalformed);
        }
    }

    private static RecoveryRecordReadResult Parse(JsonElement root)
    {
        if (!TryInt32(root, "schemaVersion", out int schemaVersion))
        {
            return SchemaFailure("schemaVersion");
        }

        if (schemaVersion != 1)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.PayloadVersionUnsupported);
        }

        if (!TryExactString(root, "writerVersion", "1.0")
            || !TryGuid(root, "recordId", requireVersion4: true, out Guid recordId)
            || !TryGuid(root, "taskId", requireVersion4: false, out Guid taskId)
            || !TryState(root, out RecoveryRecordState state)
            || !TryNormalizedPath(root, out string normalizedPath)
            || !TryVolumeSerial(root, out ulong volumeSerialNumber)
            || !TryUInt64String(root, "fileIdHigh", out ulong fileIdHigh)
            || !TryUInt64String(root, "fileIdLow", out ulong fileIdLow)
            || !TryAccountSid(root, out string accountSid)
            || !TryLogonSid(root, out string logonSid)
            || !TryUInt32(root, "windowsSessionId", allowZero: true, out uint windowsSessionId)
            || !TryExactString(root, "aceType", "Deny")
            || !TryUInt32(root, "accessMask", allowZero: false, out uint accessMask)
            || !TryFlags(root, "inheritanceFlags", out int inheritanceFlags)
            || !TryFlags(root, "propagationFlags", out int propagationFlags)
            || !TryHash(root, "aceFingerprintSha256", allowNull: false, out string? aceFingerprint)
            || !TryHash(root, "baselineDaclSha256", allowNull: false, out string? baselineDacl)
            || !TryHash(root, "postApplyDaclSha256", allowNull: true, out string? postApplyDacl)
            || !TryDate(root, "createdUtc", out DateTimeOffset createdUtc)
            || !TryDate(root, "expiresUtc", out DateTimeOffset expiresUtc)
            || !TryDate(root, "lastUpdatedUtc", out DateTimeOffset lastUpdatedUtc)
            || !TryInt32(root, "cleanupAttemptCount", out int cleanupAttemptCount)
            || cleanupAttemptCount is < 0 or > 1_000_000
            || !TryErrorCode(root, out string? lastErrorCode)
            || !TryErrorMessage(root, out string? lastErrorMessage))
        {
            return SchemaFailure();
        }

        if (createdUtc > lastUpdatedUtc || createdUtc >= expiresUtc)
        {
            return SchemaFailure();
        }

        if (!StateFieldsAreValid(
                state,
                postApplyDacl,
                cleanupAttemptCount,
                lastErrorCode,
                lastErrorMessage)
            || (state == RecoveryRecordState.Prepared && lastUpdatedUtc != createdUtc))
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.PayloadStateInvalid);
        }

        return RecoveryRecordReadResult.Success(new RecoveryRecord(
            schemaVersion,
            "1.0",
            recordId,
            taskId,
            state,
            normalizedPath,
            volumeSerialNumber,
            fileIdHigh,
            fileIdLow,
            accountSid,
            logonSid,
            windowsSessionId,
            AccessControlType.Deny,
            accessMask,
            (InheritanceFlags)inheritanceFlags,
            (PropagationFlags)propagationFlags,
            aceFingerprint!,
            baselineDacl!,
            postApplyDacl,
            createdUtc,
            expiresUtc,
            lastUpdatedUtc,
            cleanupAttemptCount,
            lastErrorCode,
            lastErrorMessage));
    }

    private static bool StateFieldsAreValid(
        RecoveryRecordState state,
        string? postApply,
        int cleanupCount,
        string? errorCode,
        string? errorMessage) => state switch
        {
            RecoveryRecordState.Prepared =>
                postApply is null && cleanupCount == 0 && errorCode is null && errorMessage is null,
            RecoveryRecordState.Applied =>
                postApply is not null && cleanupCount == 0 && errorCode is null && errorMessage is null,
            RecoveryRecordState.CleanupPending =>
                postApply is not null && cleanupCount >= 1 && errorCode is null && errorMessage is null,
            RecoveryRecordState.CleanupFailed =>
                postApply is not null && cleanupCount >= 1 && errorCode is not null && errorMessage is not null,
            _ => false,
        };

    private static bool TryGuid(JsonElement root, string name, bool requireVersion4, out Guid value)
    {
        value = default;
        if (!TryString(root, name, out string? text)
            || text.Length != 36
            || !Guid.TryParseExact(text, "D", out value)
            || value == Guid.Empty
            || value.ToString("D") != text
            || text[19] is not ('8' or '9' or 'a' or 'b'))
        {
            return false;
        }

        return !requireVersion4 || text[14] == '4';
    }

    private static bool TryState(JsonElement root, out RecoveryRecordState state)
    {
        state = default;
        return TryString(root, "state", out string? text)
            && text switch
            {
                "Prepared" => Assign(RecoveryRecordState.Prepared, out state),
                "Applied" => Assign(RecoveryRecordState.Applied, out state),
                "CleanupPending" => Assign(RecoveryRecordState.CleanupPending, out state),
                "CleanupFailed" => Assign(RecoveryRecordState.CleanupFailed, out state),
                _ => false,
            };
    }

    private static bool TryNormalizedPath(JsonElement root, out string value)
    {
        value = string.Empty;
        if (!TryString(root, "normalizedPath", out string? path)
            || path.Length is < 1 or > 32767
            || path.IndexOf('\0') >= 0
            || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path)
            || path.IndexOf(':', 2) >= 0)
        {
            return false;
        }

        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (normalized != path
                || string.Equals(normalized, Path.GetPathRoot(normalized), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            value = path;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryVolumeSerial(JsonElement root, out ulong value)
    {
        value = 0;
        return TryString(root, "volumeSerialNumber", out string? text)
            && text.Length == 16
            && text.All(IsLowerHex)
            && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryUInt64String(JsonElement root, string name, out ulong value)
    {
        value = 0;
        return TryString(root, name, out string? text)
            && text.Length > 0
            && (text == "0" || (text[0] is >= '1' and <= '9' && text.All(char.IsAsciiDigit)))
            && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryAccountSid(JsonElement root, out string value)
    {
        value = string.Empty;
        if (!TryCanonicalSid(root, "accountSid", out string sid)
            || sid.Length > 184
            || sid.StartsWith("S-1-5-5-", StringComparison.Ordinal)
            || sid.StartsWith("S-1-5-32-", StringComparison.Ordinal)
            || sid.StartsWith("S-1-5-80-", StringComparison.Ordinal)
            || sid.StartsWith("S-1-15-3-", StringComparison.Ordinal))
        {
            return false;
        }

        value = sid;
        return true;
    }

    private static bool TryLogonSid(JsonElement root, out string value)
    {
        value = string.Empty;
        if (!TryCanonicalSid(root, "logonSid", out string sid))
        {
            return false;
        }

        string[] parts = sid.Split('-');
        if (parts.Length != 6 || parts[0] != "S" || parts[1] != "1" || parts[2] != "5" || parts[3] != "5")
        {
            return false;
        }

        value = sid;
        return true;
    }

    private static bool TryCanonicalSid(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!TryString(root, name, out string? text) || text.Length == 0 || !text.All(char.IsAscii))
        {
            return false;
        }

        try
        {
            var sid = new SecurityIdentifier(text);
            if (sid.Value != text)
            {
                return false;
            }

            value = text;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryHash(JsonElement root, string name, bool allowNull, out string? value)
    {
        value = null;
        JsonElement property = root.GetProperty(name);
        if (allowNull && property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string text = property.GetString()!;
        if (text.Length != 64 || !text.All(IsLowerHex) || text.All(character => character == '0'))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryDate(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(root, name, out string? text)
            && DateTimeOffset.TryParseExact(
                text,
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value)
            && FormatDate(value) == text;
    }

    private static bool TryErrorCode(JsonElement root, out string? value)
    {
        value = null;
        JsonElement property = root.GetProperty("lastErrorCode");
        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string text = property.GetString()!;
        if (text.Length is < 1 or > 128 || !IsErrorCode(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryErrorMessage(JsonElement root, out string? value)
    {
        value = null;
        JsonElement property = root.GetProperty("lastErrorMessage");
        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string text = property.GetString()!;
        if (text.Length == 0
            || text.EnumerateRunes().Count() > 256
            || text.Any(character => char.IsControl(character)))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryFlags(JsonElement root, string name, out int value) =>
        TryInt32(root, name, out value) && (value & ~3) == 0;

    private static bool TryUInt32(JsonElement root, string name, bool allowZero, out uint value)
    {
        value = 0;
        JsonElement property = root.GetProperty(name);
        string raw = property.GetRawText();
        return property.ValueKind == JsonValueKind.Number
            && raw.Length > 0
            && raw.All(char.IsAsciiDigit)
            && uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && (allowZero || value != 0);
    }

    private static bool TryInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        JsonElement property = root.GetProperty(name);
        string raw = property.GetRawText();
        return property.ValueKind == JsonValueKind.Number
            && raw.Length > 0
            && (raw.All(char.IsAsciiDigit)
                || (raw[0] == '-' && raw.Length > 1 && raw[1..].All(char.IsAsciiDigit)))
            && int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryExactString(JsonElement root, string name, string expected) =>
        TryString(root, name, out string? value) && value == expected;

    private static bool TryString(JsonElement root, string name, out string value)
    {
        JsonElement property = root.GetProperty(name);
        value = property.ValueKind == JsonValueKind.String ? property.GetString()! : string.Empty;
        return property.ValueKind == JsonValueKind.String;
    }

    private static bool Assign(RecoveryRecordState value, out RecoveryRecordState target)
    {
        target = value;
        return true;
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsErrorCode(string value)
    {
        const string prefix = "FSL_E_";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string suffix = value[prefix.Length..];
        return suffix.Length > 0
            && suffix[0] != '_'
            && suffix[^1] != '_'
            && !suffix.Contains("__", StringComparison.Ordinal)
            && suffix.All(character => character == '_' || character is >= 'A' and <= 'Z' or >= '0' and <= '9');
    }

    private static string FormatGuid(Guid value) => value.ToString("D");

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(DateFormat, CultureInfo.InvariantCulture);

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static RecoveryRecordReadResult SchemaFailure(string? field = null) =>
        RecoveryRecordReadResult.Failure(RecoveryRecordErrors.PayloadSchemaInvalid(field));
}
