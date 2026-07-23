using System.Globalization;
using System.Text.Json;
using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Protocol;

public static class BrokerHandshakeProtocolJson
{
    private static readonly HashSet<string> ClientHelloFields =
    [
        "frameType",
        "handshakeVersion",
        "protocolVersion",
        "requestId",
        "command",
        "claimedClientProcessId",
        "clientSessionId",
        "clientNonce",
        "sentAtUtc",
    ];

    private static readonly HashSet<string> ServerHelloFields =
    [
        "frameType",
        "handshakeVersion",
        "protocolVersion",
        "requestId",
        "command",
        "success",
        "serverTimeUtc",
        "result",
        "error",
    ];

    private static readonly HashSet<string> ServerHelloResultFields =
    [
        "connectionId",
        "serverNonce",
        "expiresUtc",
    ];

    private static readonly HashSet<string> CommandRequestFields =
    [
        "frameType",
        "handshakeVersion",
        "protocolVersion",
        "requestId",
        "command",
        "connectionId",
        "bindingProof",
        "request",
    ];

    private static readonly HashSet<string> CommandResponseFields =
    [
        "frameType",
        "handshakeVersion",
        "protocolVersion",
        "requestId",
        "command",
        "connectionId",
        "response",
    ];

    private static readonly HashSet<string> ErrorFields =
    [
        "code",
        "message",
        "retryable",
        "field",
    ];

    public static BrokerHandshakeFrameParseResult DeserializeFrame(
        ReadOnlyMemory<byte> utf8Json,
        DateTimeOffset serverTimeUtc,
        LockDurationPolicy durationPolicy)
    {
        ArgumentNullException.ThrowIfNull(durationPolicy);
        if (!TryParseDocument(utf8Json, out JsonDocument? document))
        {
            return Malformed(null, null);
        }

        using (JsonDocument parsedDocument = document!)
        {
            JsonElement root = parsedDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("frameType", out JsonElement frameTypeElement)
                || frameTypeElement.ValueKind != JsonValueKind.String)
            {
                return Malformed(ParsedRequestId(root), ParsedCommand(root));
            }

            Guid? requestId = ParsedRequestId(root);
            BrokerCommand? command = ParsedCommand(root);
            return frameTypeElement.GetString() switch
            {
                BrokerProtocolConstants.ClientHello => ParseClientHello(root, requestId, command),
                BrokerProtocolConstants.ServerHello => ParseServerHello(root, requestId, command),
                BrokerProtocolConstants.CommandRequest => ParseCommandRequest(
                    root,
                    requestId,
                    command,
                    serverTimeUtc,
                    durationPolicy),
                BrokerProtocolConstants.CommandResponse => ParseCommandResponse(root, requestId, command),
                _ => Failure(SchemaError("frameType"), requestId, command),
            };
        }
    }

    public static byte[] SerializeFrame(object frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            switch (frame)
            {
                case BrokerClientHello value:
                    WriteClientHello(writer, value);
                    break;
                case BrokerServerHello value:
                    WriteServerHello(writer, value);
                    break;
                case BrokerCommandRequest value:
                    WriteCommandRequest(writer, value);
                    break;
                case BrokerCommandResponse value:
                    WriteCommandResponse(writer, value);
                    break;
                default:
                    throw new ArgumentException("The frame type is not part of protocol v1.", nameof(frame));
            }
        }

        return stream.ToArray();
    }

    private static BrokerHandshakeFrameParseResult ParseClientHello(
        JsonElement root,
        Guid? requestId,
        BrokerCommand? command)
    {
        if (HasMalformedInteger(root, "handshakeVersion", true)
            || HasMalformedInteger(root, "protocolVersion", true)
            || HasMalformedGuid(root, "requestId")
            || HasMalformedString(root, "command")
            || HasMalformedInteger(root, "claimedClientProcessId", false)
            || HasMalformedInteger(root, "clientSessionId", false)
            || HasMalformedString(root, "clientNonce")
            || HasMalformedTimestamp(root, "sentAtUtc"))
        {
            return Malformed(requestId, command);
        }

        BrokerError? schema = ValidateSchema(root, ClientHelloFields, ClientHelloFields);
        if (schema is not null)
        {
            return Failure(schema, requestId, command);
        }

        int handshakeVersion = root.GetProperty("handshakeVersion").GetInt32();
        if (handshakeVersion != BrokerProtocolConstants.HandshakeVersion)
        {
            return Failure(new BrokerError(
                BrokerErrorCodes.FSL_E_HANDSHAKE_VERSION_UNSUPPORTED,
                "The handshake version is not supported.",
                false,
                "handshakeVersion"), requestId, command);
        }

        int protocolVersion = root.GetProperty("protocolVersion").GetInt32();
        if (protocolVersion != BrokerProtocolConstants.ProtocolVersion)
        {
            return Failure(new BrokerError(
                BrokerErrorCodes.FSL_E_PROTOCOL_VERSION_UNSUPPORTED,
                "The protocol version is not supported.",
                false,
                "protocolVersion"), requestId, command);
        }

        if (command is null)
        {
            return Failure(new BrokerError(
                BrokerErrorCodes.FSL_E_UNKNOWN_COMMAND,
                "The command is not supported.",
                false,
                "command"), requestId, null);
        }

        uint claimedProcessId = root.GetProperty("claimedClientProcessId").GetUInt32();
        if (claimedProcessId == 0)
        {
            return Failure(SchemaError("claimedClientProcessId"), requestId, command);
        }

        string nonce = root.GetProperty("clientNonce").GetString()!;
        if (!BrokerHandshakeBinding.IsValidNonce(nonce))
        {
            return Malformed(requestId, command);
        }

        return BrokerHandshakeFrameParseResult.Success(new BrokerClientHello(
            BrokerFrameType.ClientHello,
            handshakeVersion,
            protocolVersion,
            requestId!.Value,
            command.Value,
            claimedProcessId,
            root.GetProperty("clientSessionId").GetUInt32(),
            nonce,
            ParseTimestamp(root.GetProperty("sentAtUtc").GetString()!)));
    }

    private static BrokerHandshakeFrameParseResult ParseServerHello(
        JsonElement root,
        Guid? requestId,
        BrokerCommand? command)
    {
        if (HasMalformedInteger(root, "handshakeVersion", true)
            || HasMalformedInteger(root, "protocolVersion", true)
            || HasMalformedNullableGuid(root, "requestId")
            || HasMalformedNullableString(root, "command")
            || HasMalformedBoolean(root, "success")
            || HasMalformedTimestamp(root, "serverTimeUtc")
            || HasMalformedKinds(root, "result", JsonValueKind.Object, JsonValueKind.Null)
            || HasMalformedKinds(root, "error", JsonValueKind.Object, JsonValueKind.Null))
        {
            return Malformed(requestId, command);
        }

        BrokerError? schema = ValidateSchema(
            root,
            ServerHelloFields,
            ServerHelloFields,
            ["requestId", "command", "result", "error"]);
        if (schema is not null)
        {
            return Failure(schema, requestId, command);
        }

        if (root.GetProperty("handshakeVersion").GetInt32() != BrokerProtocolConstants.HandshakeVersion
            || root.GetProperty("protocolVersion").GetInt32() != BrokerProtocolConstants.ProtocolVersion)
        {
            return Failure(SchemaError(null), requestId, command);
        }

        bool success = root.GetProperty("success").GetBoolean();
        JsonElement result = root.GetProperty("result");
        JsonElement error = root.GetProperty("error");
        BrokerServerHelloResult? parsedResult = null;
        BrokerError? parsedError = null;
        if (success)
        {
            if (requestId is null || command is null || result.ValueKind != JsonValueKind.Object
                || error.ValueKind != JsonValueKind.Null)
            {
                return Failure(SchemaError(null), requestId, command);
            }

            BrokerError? resultSchema = ValidateSchema(result, ServerHelloResultFields, ServerHelloResultFields);
            if (resultSchema is not null
                || HasMalformedGuid(result, "connectionId")
                || HasMalformedString(result, "serverNonce")
                || HasMalformedTimestamp(result, "expiresUtc"))
            {
                return Malformed(requestId, command);
            }

            string nonce = result.GetProperty("serverNonce").GetString()!;
            if (!BrokerHandshakeBinding.IsValidNonce(nonce))
            {
                return Malformed(requestId, command);
            }

            parsedResult = new BrokerServerHelloResult(
                ParseGuid(result.GetProperty("connectionId").GetString()!),
                nonce,
                ParseTimestamp(result.GetProperty("expiresUtc").GetString()!));
        }
        else
        {
            if (result.ValueKind != JsonValueKind.Null || error.ValueKind != JsonValueKind.Object)
            {
                return Failure(SchemaError(null), requestId, command);
            }

            parsedError = ParseError(error);
            if (parsedError is null)
            {
                return Malformed(requestId, command);
            }
        }

        return BrokerHandshakeFrameParseResult.Success(new BrokerServerHello(
            BrokerFrameType.ServerHello,
            BrokerProtocolConstants.HandshakeVersion,
            BrokerProtocolConstants.ProtocolVersion,
            requestId,
            command?.ToString(),
            success,
            ParseTimestamp(root.GetProperty("serverTimeUtc").GetString()!),
            parsedResult,
            parsedError));
    }

    private static BrokerHandshakeFrameParseResult ParseCommandRequest(
        JsonElement root,
        Guid? requestId,
        BrokerCommand? command,
        DateTimeOffset serverTimeUtc,
        LockDurationPolicy durationPolicy)
    {
        if (HasMalformedInteger(root, "handshakeVersion", true)
            || HasMalformedInteger(root, "protocolVersion", true)
            || HasMalformedGuid(root, "requestId")
            || HasMalformedString(root, "command")
            || HasMalformedGuid(root, "connectionId")
            || HasMalformedString(root, "bindingProof")
            || HasMalformedKinds(root, "request", JsonValueKind.Object))
        {
            return Malformed(requestId, command);
        }

        BrokerError? schema = ValidateSchema(root, CommandRequestFields, CommandRequestFields);
        if (schema is not null)
        {
            return Failure(schema, requestId, command);
        }

        BrokerRequestParseResult request = BrokerProtocolJson.DeserializeRequest(
            JsonBytes(root.GetProperty("request")),
            serverTimeUtc,
            durationPolicy);
        if (!request.IsSuccess)
        {
            return Failure(request.FailureResponse!.Error!, requestId, command);
        }

        if (command is null)
        {
            return Failure(new BrokerError(
                BrokerErrorCodes.FSL_E_UNKNOWN_COMMAND,
                "The command is not supported.",
                false,
                "command"), requestId, null);
        }

        return BrokerHandshakeFrameParseResult.Success(new BrokerCommandRequest(
            BrokerFrameType.CommandRequest,
            root.GetProperty("handshakeVersion").GetInt32(),
            root.GetProperty("protocolVersion").GetInt32(),
            requestId!.Value,
            command.Value,
            ParseGuid(root.GetProperty("connectionId").GetString()!),
            root.GetProperty("bindingProof").GetString()!,
            request.Request!));
    }

    private static BrokerHandshakeFrameParseResult ParseCommandResponse(
        JsonElement root,
        Guid? requestId,
        BrokerCommand? command)
    {
        if (HasMalformedInteger(root, "handshakeVersion", true)
            || HasMalformedInteger(root, "protocolVersion", true)
            || HasMalformedGuid(root, "requestId")
            || HasMalformedString(root, "command")
            || HasMalformedGuid(root, "connectionId")
            || HasMalformedKinds(root, "response", JsonValueKind.Object))
        {
            return Malformed(requestId, command);
        }

        BrokerError? schema = ValidateSchema(root, CommandResponseFields, CommandResponseFields);
        if (schema is not null)
        {
            return Failure(schema, requestId, command);
        }

        BrokerResponseParseResult response = BrokerProtocolJson.DeserializeResponse(
            JsonBytes(root.GetProperty("response")));
        if (!response.IsSuccess || command is null)
        {
            return Malformed(requestId, command);
        }

        return BrokerHandshakeFrameParseResult.Success(new BrokerCommandResponse(
            BrokerFrameType.CommandResponse,
            root.GetProperty("handshakeVersion").GetInt32(),
            root.GetProperty("protocolVersion").GetInt32(),
            requestId!.Value,
            command.Value,
            ParseGuid(root.GetProperty("connectionId").GetString()!),
            response.Response!));
    }

    private static void WriteClientHello(Utf8JsonWriter writer, BrokerClientHello value)
    {
        if (value.FrameType != BrokerFrameType.ClientHello
            || value.HandshakeVersion != BrokerProtocolConstants.HandshakeVersion
            || value.ProtocolVersion != BrokerProtocolConstants.ProtocolVersion
            || value.ClaimedClientProcessId == 0
            || !BrokerHandshakeBinding.IsValidNonce(value.ClientNonce))
        {
            throw new ArgumentException("The ClientHello does not match protocol v1.", nameof(value));
        }

        writer.WriteStartObject();
        writer.WriteString("frameType", BrokerProtocolConstants.ClientHello);
        writer.WriteNumber("handshakeVersion", value.HandshakeVersion);
        writer.WriteNumber("protocolVersion", value.ProtocolVersion);
        writer.WriteString("requestId", FormatGuid(value.RequestId));
        writer.WriteString("command", value.Command.ToString());
        writer.WriteNumber("claimedClientProcessId", value.ClaimedClientProcessId);
        writer.WriteNumber("clientSessionId", value.ClientSessionId);
        writer.WriteString("clientNonce", value.ClientNonce);
        writer.WriteString("sentAtUtc", FormatTimestamp(value.SentAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteServerHello(Utf8JsonWriter writer, BrokerServerHello value)
    {
        if (value.FrameType != BrokerFrameType.ServerHello
            || value.HandshakeVersion != BrokerProtocolConstants.HandshakeVersion
            || value.ProtocolVersion != BrokerProtocolConstants.ProtocolVersion
            || value.Success == (value.Result is null)
            || value.Success == (value.Error is not null))
        {
            throw new ArgumentException("The ServerHello does not match protocol v1.", nameof(value));
        }

        writer.WriteStartObject();
        writer.WriteString("frameType", BrokerProtocolConstants.ServerHello);
        writer.WriteNumber("handshakeVersion", value.HandshakeVersion);
        writer.WriteNumber("protocolVersion", value.ProtocolVersion);
        WriteNullableGuid(writer, "requestId", value.RequestId);
        WriteNullableString(writer, "command", value.Command);
        writer.WriteBoolean("success", value.Success);
        writer.WriteString("serverTimeUtc", FormatTimestamp(value.ServerTimeUtc));
        writer.WritePropertyName("result");
        if (value.Result is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("connectionId", FormatGuid(value.Result.ConnectionId));
            writer.WriteString("serverNonce", value.Result.ServerNonce);
            writer.WriteString("expiresUtc", FormatTimestamp(value.Result.ExpiresUtc));
            writer.WriteEndObject();
        }

        writer.WritePropertyName("error");
        if (value.Error is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteError(writer, value.Error);
        }

        writer.WriteEndObject();
    }

    private static void WriteCommandRequest(Utf8JsonWriter writer, BrokerCommandRequest value)
    {
        writer.WriteStartObject();
        writer.WriteString("frameType", BrokerProtocolConstants.CommandRequest);
        writer.WriteNumber("handshakeVersion", value.HandshakeVersion);
        writer.WriteNumber("protocolVersion", value.ProtocolVersion);
        writer.WriteString("requestId", FormatGuid(value.RequestId));
        writer.WriteString("command", value.Command.ToString());
        writer.WriteString("connectionId", FormatGuid(value.ConnectionId));
        writer.WriteString("bindingProof", value.BindingProof);
        writer.WritePropertyName("request");
        writer.WriteRawValue(BrokerProtocolJson.SerializeRequest(value.Request), skipInputValidation: false);
        writer.WriteEndObject();
    }

    private static void WriteCommandResponse(Utf8JsonWriter writer, BrokerCommandResponse value)
    {
        writer.WriteStartObject();
        writer.WriteString("frameType", BrokerProtocolConstants.CommandResponse);
        writer.WriteNumber("handshakeVersion", value.HandshakeVersion);
        writer.WriteNumber("protocolVersion", value.ProtocolVersion);
        writer.WriteString("requestId", FormatGuid(value.RequestId));
        writer.WriteString("command", value.Command.ToString());
        writer.WriteString("connectionId", FormatGuid(value.ConnectionId));
        writer.WritePropertyName("response");
        writer.WriteRawValue(BrokerProtocolJson.SerializeResponse(value.Response), skipInputValidation: false);
        writer.WriteEndObject();
    }

    private static BrokerError? ParseError(JsonElement element)
    {
        BrokerError? schema = ValidateSchema(element, ErrorFields, ErrorFields, ["field"]);
        if (schema is not null
            || HasMalformedString(element, "code")
            || HasMalformedString(element, "message")
            || HasMalformedBoolean(element, "retryable")
            || HasMalformedNullableString(element, "field"))
        {
            return null;
        }

        try
        {
            return new BrokerError(
                element.GetProperty("code").GetString()!,
                element.GetProperty("message").GetString()!,
                element.GetProperty("retryable").GetBoolean(),
                element.GetProperty("field").ValueKind == JsonValueKind.Null
                    ? null
                    : element.GetProperty("field").GetString());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static BrokerError? ValidateSchema(
        JsonElement element,
        HashSet<string> allowed,
        HashSet<string> required,
        HashSet<string>? nullable = null)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || (property.Value.ValueKind == JsonValueKind.Null
                    && (nullable is null || !nullable.Contains(property.Name))))
            {
                return SchemaError(property.Name);
            }
        }

        return required.FirstOrDefault(name => !element.TryGetProperty(name, out _)) is string missing
            ? SchemaError(missing)
            : null;
    }

    private static bool TryParseDocument(ReadOnlyMemory<byte> utf8Json, out JsonDocument? document)
    {
        document = null;
        try
        {
            var reader = new Utf8JsonReader(utf8Json.Span, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var properties = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    properties.Push(new HashSet<string>(StringComparer.Ordinal));
                }
                else if (reader.TokenType == JsonTokenType.PropertyName
                    && (properties.Count == 0 || !properties.Peek().Add(reader.GetString()!)))
                {
                    return false;
                }
                else if (reader.TokenType == JsonTokenType.EndObject)
                {
                    properties.Pop();
                }
            }

            document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Guid? ParsedRequestId(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("requestId", out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && TryParseGuid(value.GetString()!, out Guid parsed)
            ? parsed
            : null;

    private static BrokerCommand? ParsedCommand(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("command", out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && TryParseCommand(value.GetString()!, out BrokerCommand parsed)
            ? parsed
            : null;

    private static bool HasMalformedKinds(JsonElement root, string name, params JsonValueKind[] kinds) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind != JsonValueKind.Null
        && !kinds.Contains(value.ValueKind);

    private static bool HasMalformedString(JsonElement root, string name) =>
        HasMalformedKinds(root, name, JsonValueKind.String);

    private static bool HasMalformedNullableString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null);

    private static bool HasMalformedBoolean(JsonElement root, string name) =>
        HasMalformedKinds(root, name, JsonValueKind.True, JsonValueKind.False);

    private static bool HasMalformedGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && (value.ValueKind != JsonValueKind.String || !TryParseGuid(value.GetString()!, out _));

    private static bool HasMalformedNullableGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind != JsonValueKind.Null
        && (value.ValueKind != JsonValueKind.String || !TryParseGuid(value.GetString()!, out _));

    private static bool HasMalformedTimestamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && (value.ValueKind != JsonValueKind.String || !TryParseTimestamp(value.GetString()!, out _));

    private static bool HasMalformedInteger(JsonElement root, string name, bool signed)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        string raw = value.GetRawText();
        return value.ValueKind != JsonValueKind.Number
            || raw.IndexOfAny(['.', 'e', 'E', '+']) >= 0
            || (signed ? !value.TryGetInt32(out _) : !value.TryGetUInt32(out _));
    }

    private static bool TryParseGuid(string value, out Guid guid) =>
        Guid.TryParseExact(value, BrokerProtocolConstants.GuidFormat, out guid)
        && guid != Guid.Empty
        && string.Equals(value, guid.ToString("D"), StringComparison.Ordinal);

    private static Guid ParseGuid(string value) => TryParseGuid(value, out Guid result)
        ? result
        : throw new JsonException();

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(
            value,
            BrokerProtocolConstants.UtcTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp)
        && timestamp.Offset == TimeSpan.Zero;

    private static DateTimeOffset ParseTimestamp(string value) =>
        TryParseTimestamp(value, out DateTimeOffset result) ? result : throw new JsonException();

    private static bool TryParseCommand(string value, out BrokerCommand command)
    {
        command = value switch
        {
            BrokerProtocolConstants.ValidatePath => BrokerCommand.ValidatePath,
            BrokerProtocolConstants.CreateLock => BrokerCommand.CreateLock,
            BrokerProtocolConstants.RemoveLock => BrokerCommand.RemoveLock,
            BrokerProtocolConstants.GetStatus => BrokerCommand.GetStatus,
            _ => (BrokerCommand)(-1),
        };
        return Enum.IsDefined(command);
    }

    private static byte[] JsonBytes(JsonElement element) =>
        System.Text.Encoding.UTF8.GetBytes(element.GetRawText());

    private static string FormatGuid(Guid value) => value != Guid.Empty
        ? value.ToString("D")
        : throw new ArgumentException("Protocol Guid values cannot be empty.", nameof(value));

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString(
        BrokerProtocolConstants.UtcTimestampFormat,
        CultureInfo.InvariantCulture);

    private static void WriteNullableGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, FormatGuid(value.Value));
        }
    }

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

    private static void WriteError(Utf8JsonWriter writer, BrokerError error)
    {
        writer.WriteStartObject();
        writer.WriteString("code", error.Code);
        writer.WriteString("message", error.Message);
        writer.WriteBoolean("retryable", error.Retryable);
        WriteNullableString(writer, "field", error.Field);
        writer.WriteEndObject();
    }

    private static BrokerHandshakeFrameParseResult Malformed(Guid? requestId, BrokerCommand? command) =>
        Failure(new BrokerError(
            BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE,
            "The handshake message is malformed.",
            false,
            null), requestId, command);

    private static BrokerError SchemaError(string? field) => new(
        BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION,
        "The request does not match the protocol schema.",
        false,
        field);

    private static BrokerHandshakeFrameParseResult Failure(
        BrokerError error,
        Guid? requestId,
        BrokerCommand? command) => BrokerHandshakeFrameParseResult.Failure(
            error,
            requestId,
            command);
}
