using System.Globalization;
using System.Text.Json;
using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Protocol;

public static class BrokerProtocolJson
{
    private static readonly HashSet<string> EnvelopeFields =
    [
        "protocolVersion",
        "requestId",
        "command",
        "clientSessionId",
        "sentAtUtc",
        "payload",
    ];

    private static readonly HashSet<string> ResponseFields =
    [
        "protocolVersion",
        "requestId",
        "command",
        "success",
        "serverTimeUtc",
        "result",
        "error",
    ];

    private static readonly HashSet<string> ForbiddenFields =
    [
        "sid",
        "accountSid",
        "logonSid",
        "accessMask",
        "acl",
        "ace",
        "sddl",
        "recoveryPath",
        "installationPath",
        "serviceName",
        "pipeName",
        "shell",
        "powerShell",
        "cmd",
        "script",
        "executablePath",
        "lockRemovalIntent",
        "removalIntent",
        "intent",
        "cleanupMode",
        "reason",
        "force",
    ];

    public static BrokerRequestParseResult DeserializeRequest(
        ReadOnlyMemory<byte> utf8Json,
        DateTimeOffset serverTimeUtc,
        LockDurationPolicy durationPolicy)
    {
        ArgumentNullException.ThrowIfNull(durationPolicy);

        if (!TryParseDocument(utf8Json, out JsonDocument? document))
        {
            return BrokerRequestParseResult.Failure(BrokerResponseEnvelope.Malformed(serverTimeUtc));
        }

        using (JsonDocument parsedDocument = document!)
        {
            JsonElement root = parsedDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || HasMalformedEnvelopeType(root, out Guid requestId, out BrokerCommand? parsedCommand))
            {
                return BrokerRequestParseResult.Failure(BrokerResponseEnvelope.Malformed(serverTimeUtc));
            }

            if (TryFindForbiddenEnvelopeField(root, out string? forbiddenField))
            {
                return Failure(
                    requestId,
                    parsedCommand,
                    serverTimeUtc,
                    new BrokerError(
                        BrokerErrorCodes.FSL_E_FORBIDDEN_INPUT,
                        "The request contains an input that clients are not allowed to control.",
                        false,
                        forbiddenField));
            }

            BrokerError? schemaError = ValidateSchema(root, EnvelopeFields, EnvelopeFields);
            if (schemaError is not null)
            {
                return Failure(requestId, parsedCommand, serverTimeUtc, schemaError);
            }

            int protocolVersion = root.GetProperty("protocolVersion").GetInt32();
            if (protocolVersion != BrokerProtocolConstants.ProtocolVersion)
            {
                return Failure(
                    requestId,
                    parsedCommand,
                    serverTimeUtc,
                    BrokerErrorCodes.FSL_E_PROTOCOL_VERSION_UNSUPPORTED,
                    "The protocol version is not supported.",
                    "protocolVersion");
            }

            string commandText = root.GetProperty("command").GetString()!;
            if (!TryParseCommand(commandText, out BrokerCommand command))
            {
                return Failure(
                    requestId,
                    null,
                    serverTimeUtc,
                    BrokerErrorCodes.FSL_E_UNKNOWN_COMMAND,
                    "The command is not supported.",
                    "command");
            }

            JsonElement payload = root.GetProperty("payload");
            PayloadParseResult payloadResult = ParsePayload(command, payload, durationPolicy);
            if (payloadResult.Error is not null)
            {
                return Failure(requestId, command, serverTimeUtc, payloadResult.Error);
            }

            return BrokerRequestParseResult.Success(new BrokerRequestEnvelope(
                protocolVersion,
                requestId,
                command,
                root.GetProperty("clientSessionId").GetUInt32(),
                ParseTimestamp(root.GetProperty("sentAtUtc").GetString()!),
                payloadResult.Payload!));
        }
    }

    public static BrokerResponseParseResult DeserializeResponse(ReadOnlyMemory<byte> utf8Json)
    {
        if (!TryParseDocument(utf8Json, out JsonDocument? document))
        {
            return BrokerResponseParseResult.Failure(MalformedError());
        }

        using (JsonDocument parsedDocument = document!)
        {
            JsonElement root = parsedDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasMalformedResponseType(root))
            {
                return BrokerResponseParseResult.Failure(MalformedError());
            }

            BrokerError? schemaError = ValidateSchema(
                root,
                ResponseFields,
                ResponseFields,
                ["requestId", "command", "result", "error"]);
            if (schemaError is not null)
            {
                return BrokerResponseParseResult.Failure(schemaError);
            }

            if (root.GetProperty("protocolVersion").GetInt32() != BrokerProtocolConstants.ProtocolVersion)
            {
                return BrokerResponseParseResult.Failure(new BrokerError(
                    BrokerErrorCodes.FSL_E_PROTOCOL_VERSION_UNSUPPORTED,
                    "The protocol version is not supported.",
                    false,
                    "protocolVersion"));
            }

            bool success = root.GetProperty("success").GetBoolean();
            JsonElement requestIdElement = root.GetProperty("requestId");
            JsonElement commandElement = root.GetProperty("command");
            Guid? requestId = requestIdElement.ValueKind == JsonValueKind.Null
                ? null
                : ParseGuid(requestIdElement.GetString()!);
            BrokerCommand? command = null;
            if (commandElement.ValueKind != JsonValueKind.Null)
            {
                if (!TryParseCommand(commandElement.GetString()!, out BrokerCommand parsedCommand))
                {
                    return BrokerResponseParseResult.Failure(SchemaError("command"));
                }

                command = parsedCommand;
            }

            DateTimeOffset serverTimeUtc = ParseTimestamp(root.GetProperty("serverTimeUtc").GetString()!);
            JsonElement resultElement = root.GetProperty("result");
            JsonElement errorElement = root.GetProperty("error");

            if (success)
            {
                if (requestId is null
                    || command is null
                    || resultElement.ValueKind != JsonValueKind.Object
                    || errorElement.ValueKind != JsonValueKind.Null)
                {
                    return BrokerResponseParseResult.Failure(SchemaError(null));
                }

                ResultParseResult result = ParseResult(command.Value, resultElement);
                if (result.Error is not null)
                {
                    return BrokerResponseParseResult.Failure(result.Error);
                }

                return BrokerResponseParseResult.Success(BrokerResponseEnvelope.Succeeded(
                    requestId.Value,
                    command.Value,
                    serverTimeUtc,
                    result.Value!));
            }

            if (resultElement.ValueKind != JsonValueKind.Null || errorElement.ValueKind != JsonValueKind.Object)
            {
                return BrokerResponseParseResult.Failure(SchemaError(null));
            }

            BrokerErrorParseResult parsedError = ParseBrokerError(errorElement);
            if (parsedError.Error is not null)
            {
                return BrokerResponseParseResult.Failure(parsedError.Error);
            }

            return BrokerResponseParseResult.Success(BrokerResponseEnvelope.Failed(
                requestId,
                command,
                serverTimeUtc,
                parsedError.Value!));
        }
    }

    public static byte[] SerializeRequest(BrokerRequestEnvelope request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != BrokerProtocolConstants.ProtocolVersion)
        {
            throw new ArgumentException("The request protocol version is not supported.", nameof(request));
        }

        if (!PayloadMatchesCommand(request.Command, request.Payload))
        {
            throw new ArgumentException("The payload type does not match the request command.", nameof(request));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", request.ProtocolVersion);
            writer.WriteString("requestId", FormatGuid(request.RequestId));
            writer.WriteString("command", CommandName(request.Command));
            writer.WriteNumber("clientSessionId", request.ClientSessionId);
            writer.WriteString("sentAtUtc", FormatTimestamp(request.SentAtUtc));
            writer.WritePropertyName("payload");
            WritePayload(writer, request.Payload);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static byte[] SerializeResponse(BrokerResponseEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", response.ProtocolVersion);
            WriteNullableGuid(writer, "requestId", response.RequestId);
            WriteNullableString(writer, "command", response.Command);
            writer.WriteBoolean("success", response.Success);
            writer.WriteString("serverTimeUtc", FormatTimestamp(response.ServerTimeUtc));
            writer.WritePropertyName("result");
            if (response.Result is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteResult(writer, response.Result);
            }

            writer.WritePropertyName("error");
            if (response.Error is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteError(writer, response.Error);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static PayloadParseResult ParsePayload(
        BrokerCommand command,
        JsonElement payload,
        LockDurationPolicy durationPolicy) => command switch
        {
            BrokerCommand.ValidatePath => ParseValidatePath(payload),
            BrokerCommand.CreateLock => ParseCreateLock(payload, durationPolicy),
            BrokerCommand.RemoveLock => ParseRemoveLock(payload),
            BrokerCommand.GetStatus => ParseGetStatus(payload),
            _ => new PayloadParseResult(null, SchemaError("command")),
        };

    private static PayloadParseResult ParseValidatePath(JsonElement payload)
    {
        HashSet<string> fields = ["path"];
        if (HasMalformedPropertyType(payload, "path", JsonValueKind.String))
        {
            return MalformedPayload();
        }

        HashSet<string> forbidden =
        [
            "normalizedPath",
            "volumeSerialNumber",
            "fileId",
            "fileIdHigh",
            "fileIdLow",
            "isReparsePoint",
            "reparseInfo",
        ];
        if (TryFindForbiddenField(payload, ForbiddenFields, out string? forbiddenField, "payload")
            || TryFindForbiddenField(payload, forbidden, out forbiddenField, "payload"))
        {
            return ForbiddenPayload(forbiddenField);
        }

        BrokerError? schemaError = ValidateSchema(payload, fields, fields);
        if (schemaError is not null)
        {
            return new PayloadParseResult(null, PrefixPayloadField(schemaError));
        }

        string path = payload.GetProperty("path").GetString()!;
        BrokerError? pathError = ValidatePath(path);
        return pathError is null
            ? new PayloadParseResult(new ValidatePathRequest(path), null)
            : new PayloadParseResult(null, pathError);
    }

    private static PayloadParseResult ParseCreateLock(
        JsonElement payload,
        LockDurationPolicy durationPolicy)
    {
        HashSet<string> fields = ["taskId", "path", "durationMilliseconds"];
        if (HasMalformedPropertyType(payload, "taskId", JsonValueKind.String)
            || HasMalformedGuid(payload, "taskId")
            || HasMalformedPropertyType(payload, "path", JsonValueKind.String)
            || HasMalformedInteger(payload, "durationMilliseconds", IntegerType.Int64))
        {
            return MalformedPayload();
        }

        HashSet<string> forbidden = ["expiresUtc", "startedUtc", "remainingMilliseconds"];
        if (TryFindForbiddenField(payload, ForbiddenFields, out string? forbiddenField, "payload")
            || TryFindForbiddenField(payload, forbidden, out forbiddenField, "payload"))
        {
            return ForbiddenPayload(forbiddenField);
        }

        BrokerError? schemaError = ValidateSchema(payload, fields, fields);
        if (schemaError is not null)
        {
            return new PayloadParseResult(null, PrefixPayloadField(schemaError));
        }

        Guid taskId = ParseGuid(payload.GetProperty("taskId").GetString()!);
        string path = payload.GetProperty("path").GetString()!;
        BrokerError? pathError = ValidatePath(path);
        if (pathError is not null)
        {
            return new PayloadParseResult(null, pathError);
        }

        long durationMilliseconds = payload.GetProperty("durationMilliseconds").GetInt64();
        var request = new CreateLockRequest(taskId, path, durationMilliseconds);
        if (durationMilliseconds <= 0 || request.ToDomain(durationPolicy).IsFailure)
        {
            return new PayloadParseResult(null, new BrokerError(
                BrokerErrorCodes.FSL_E_DURATION_OUT_OF_RANGE,
                "The lock duration is outside the allowed range.",
                false,
                "payload.durationMilliseconds"));
        }

        return new PayloadParseResult(request, null);
    }

    private static PayloadParseResult ParseRemoveLock(JsonElement payload)
    {
        HashSet<string> fields = ["taskId", "recoveryRecordId"];
        if (HasMalformedPropertyType(payload, "taskId", JsonValueKind.String)
            || HasMalformedGuid(payload, "taskId")
            || HasMalformedPropertyType(payload, "recoveryRecordId", JsonValueKind.String)
            || HasMalformedGuid(payload, "recoveryRecordId"))
        {
            return MalformedPayload();
        }

        if (TryFindForbiddenField(payload, ForbiddenFields, out string? forbiddenField, "payload"))
        {
            return ForbiddenPayload(forbiddenField);
        }

        BrokerError? schemaError = ValidateSchema(payload, fields, fields);
        if (schemaError is not null)
        {
            return new PayloadParseResult(null, PrefixPayloadField(schemaError));
        }

        return new PayloadParseResult(new RemoveLockRequest(
            ParseGuid(payload.GetProperty("taskId").GetString()!),
            ParseGuid(payload.GetProperty("recoveryRecordId").GetString()!)), null);
    }

    private static PayloadParseResult ParseGetStatus(JsonElement payload)
    {
        HashSet<string> fields = ["queryType", "taskId"];
        if (HasMalformedPropertyType(payload, "queryType", JsonValueKind.String)
            || HasMalformedNullableGuid(payload, "taskId"))
        {
            return MalformedPayload();
        }

        if (TryFindForbiddenField(payload, ForbiddenFields, out string? forbiddenField, "payload"))
        {
            return ForbiddenPayload(forbiddenField);
        }

        BrokerError? schemaError = ValidateSchema(payload, fields, fields, ["taskId"]);
        if (schemaError is not null)
        {
            return new PayloadParseResult(null, PrefixPayloadField(schemaError));
        }

        string queryTypeText = payload.GetProperty("queryType").GetString()!;
        GetStatusQueryType queryType = queryTypeText switch
        {
            "ByTaskId" => GetStatusQueryType.ByTaskId,
            "CurrentSession" => GetStatusQueryType.CurrentSession,
            _ => (GetStatusQueryType)(-1),
        };
        if (!Enum.IsDefined(queryType))
        {
            return new PayloadParseResult(null, SchemaError("payload.queryType"));
        }

        JsonElement taskIdElement = payload.GetProperty("taskId");
        Guid? taskId = taskIdElement.ValueKind == JsonValueKind.Null
            ? null
            : ParseGuid(taskIdElement.GetString()!);
        if ((queryType == GetStatusQueryType.ByTaskId && taskId is null)
            || (queryType == GetStatusQueryType.CurrentSession && taskId is not null))
        {
            return new PayloadParseResult(null, SchemaError("payload.taskId"));
        }

        return new PayloadParseResult(new GetStatusRequest(queryType, taskId), null);
    }

    private static ResultParseResult ParseResult(BrokerCommand command, JsonElement result) => command switch
    {
        BrokerCommand.ValidatePath => ParseValidatePathResult(result),
        BrokerCommand.CreateLock => ParseCreateLockResult(result),
        BrokerCommand.RemoveLock => ParseRemoveLockResult(result),
        BrokerCommand.GetStatus => ParseGetStatusResult(result),
        _ => new ResultParseResult(null, SchemaError("command")),
    };

    private static ResultParseResult ParseValidatePathResult(JsonElement result)
    {
        HashSet<string> fields =
        [
            "normalizedPath",
            "volumeRoot",
            "volumeSerialNumber",
            "fileIdHigh",
            "fileIdLow",
            "fileSystem",
            "driveType",
            "isReparsePoint",
            "isAllowed",
        ];
        if (HasMalformedProperties(result, fields, JsonValueKind.String, ["isReparsePoint", "isAllowed"])
            || HasMalformedPropertyType(result, "isReparsePoint", JsonValueKind.False, JsonValueKind.True)
            || HasMalformedPropertyType(result, "isAllowed", JsonValueKind.False, JsonValueKind.True))
        {
            return MalformedResult();
        }

        BrokerError? schemaError = ValidateSchema(result, fields, fields);
        if (schemaError is not null)
        {
            return new ResultParseResult(null, schemaError);
        }

        string serial = result.GetProperty("volumeSerialNumber").GetString()!;
        string high = result.GetProperty("fileIdHigh").GetString()!;
        string low = result.GetProperty("fileIdLow").GetString()!;
        if (serial.Length != 16
            || serial.Any(character => !IsLowerHex(character))
            || !IsCanonicalUInt64Decimal(high)
            || !IsCanonicalUInt64Decimal(low))
        {
            return MalformedResult();
        }

        if (result.GetProperty("fileSystem").GetString() != "NTFS"
            || result.GetProperty("driveType").GetString() != "Fixed"
            || result.GetProperty("isReparsePoint").GetBoolean()
            || !result.GetProperty("isAllowed").GetBoolean())
        {
            return new ResultParseResult(null, SchemaError(null));
        }

        return new ResultParseResult(new ValidatePathResult(
            result.GetProperty("normalizedPath").GetString()!,
            result.GetProperty("volumeRoot").GetString()!,
            serial,
            high,
            low,
            "NTFS",
            "Fixed",
            false,
            true), null);
    }

    private static ResultParseResult ParseCreateLockResult(JsonElement result)
    {
        HashSet<string> fields =
        [
            "taskId",
            "normalizedPath",
            "status",
            "startedUtc",
            "expiresUtc",
            "durationMilliseconds",
            "remainingMilliseconds",
            "recoveryRecordId",
            "idempotentReplay",
        ];
        if (HasMalformedPropertyType(result, "taskId", JsonValueKind.String)
            || HasMalformedGuid(result, "taskId")
            || HasMalformedPropertyType(result, "normalizedPath", JsonValueKind.String)
            || HasMalformedPropertyType(result, "status", JsonValueKind.String)
            || HasMalformedPropertyType(result, "startedUtc", JsonValueKind.String)
            || HasMalformedTimestamp(result, "startedUtc")
            || HasMalformedPropertyType(result, "expiresUtc", JsonValueKind.String)
            || HasMalformedTimestamp(result, "expiresUtc")
            || HasMalformedInteger(result, "durationMilliseconds", IntegerType.Int64)
            || HasMalformedInteger(result, "remainingMilliseconds", IntegerType.Int64)
            || HasMalformedPropertyType(result, "recoveryRecordId", JsonValueKind.String)
            || HasMalformedGuid(result, "recoveryRecordId")
            || HasMalformedPropertyType(result, "idempotentReplay", JsonValueKind.False, JsonValueKind.True))
        {
            return MalformedResult();
        }

        BrokerError? schemaError = ValidateSchema(result, fields, fields);
        if (schemaError is not null)
        {
            return new ResultParseResult(null, schemaError);
        }

        if (result.GetProperty("status").GetString() != "Active")
        {
            return new ResultParseResult(null, SchemaError("status"));
        }

        return new ResultParseResult(new CreateLockResult(
            ParseGuid(result.GetProperty("taskId").GetString()!),
            result.GetProperty("normalizedPath").GetString()!,
            LockTaskStatus.Active,
            ParseTimestamp(result.GetProperty("startedUtc").GetString()!),
            ParseTimestamp(result.GetProperty("expiresUtc").GetString()!),
            result.GetProperty("durationMilliseconds").GetInt64(),
            result.GetProperty("remainingMilliseconds").GetInt64(),
            ParseGuid(result.GetProperty("recoveryRecordId").GetString()!),
            result.GetProperty("idempotentReplay").GetBoolean()), null);
    }

    private static ResultParseResult ParseRemoveLockResult(JsonElement result)
    {
        HashSet<string> fields =
        [
            "taskId",
            "recoveryRecordId",
            "removalIntent",
            "previousStatus",
            "status",
            "removedUtc",
            "aceRemoved",
            "recoveryRecordDeleted",
            "idempotentReplay",
        ];
        if (HasMalformedPropertyType(result, "taskId", JsonValueKind.String)
            || HasMalformedGuid(result, "taskId")
            || HasMalformedPropertyType(result, "recoveryRecordId", JsonValueKind.String)
            || HasMalformedGuid(result, "recoveryRecordId")
            || HasMalformedPropertyType(result, "removalIntent", JsonValueKind.String)
            || HasMalformedPropertyType(result, "previousStatus", JsonValueKind.String)
            || HasMalformedPropertyType(result, "status", JsonValueKind.String)
            || HasMalformedPropertyType(result, "removedUtc", JsonValueKind.String)
            || HasMalformedTimestamp(result, "removedUtc")
            || HasMalformedPropertyType(result, "aceRemoved", JsonValueKind.False, JsonValueKind.True)
            || HasMalformedPropertyType(result, "recoveryRecordDeleted", JsonValueKind.False, JsonValueKind.True)
            || HasMalformedPropertyType(result, "idempotentReplay", JsonValueKind.False, JsonValueKind.True))
        {
            return MalformedResult();
        }

        BrokerError? schemaError = ValidateSchema(result, fields, fields);
        if (schemaError is not null)
        {
            return new ResultParseResult(null, schemaError);
        }

        if (!TryParseRemovalIntent(result.GetProperty("removalIntent").GetString()!, out LockRemovalIntent intent)
            || !TryParseLockTaskStatus(result.GetProperty("previousStatus").GetString()!, out LockTaskStatus previousStatus)
            || result.GetProperty("status").GetString() != "Completed")
        {
            return new ResultParseResult(null, SchemaError(null));
        }

        return new ResultParseResult(new RemoveLockResult(
            ParseGuid(result.GetProperty("taskId").GetString()!),
            ParseGuid(result.GetProperty("recoveryRecordId").GetString()!),
            intent,
            previousStatus,
            LockTaskStatus.Completed,
            ParseTimestamp(result.GetProperty("removedUtc").GetString()!),
            result.GetProperty("aceRemoved").GetBoolean(),
            result.GetProperty("recoveryRecordDeleted").GetBoolean(),
            result.GetProperty("idempotentReplay").GetBoolean()), null);
    }

    private static ResultParseResult ParseGetStatusResult(JsonElement result)
    {
        HashSet<string> fields = ["queryType", "tasks"];
        if (HasMalformedPropertyType(result, "queryType", JsonValueKind.String)
            || HasMalformedPropertyType(result, "tasks", JsonValueKind.Array))
        {
            return MalformedResult();
        }

        BrokerError? schemaError = ValidateSchema(result, fields, fields);
        if (schemaError is not null)
        {
            return new ResultParseResult(null, schemaError);
        }

        if (!TryParseQueryType(result.GetProperty("queryType").GetString()!, out GetStatusQueryType queryType))
        {
            return new ResultParseResult(null, SchemaError("queryType"));
        }

        var tasks = new List<TaskStatusItem>();
        foreach (JsonElement task in result.GetProperty("tasks").EnumerateArray())
        {
            TaskStatusParseResult parsedTask = ParseTaskStatusItem(task);
            if (parsedTask.Error is not null)
            {
                return new ResultParseResult(null, parsedTask.Error);
            }

            tasks.Add(parsedTask.Value!);
        }

        return new ResultParseResult(new GetStatusResult(queryType, tasks), null);
    }

    private static TaskStatusParseResult ParseTaskStatusItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new TaskStatusParseResult(null, MalformedError());
        }

        HashSet<string> fields =
        [
            "taskId",
            "normalizedPath",
            "status",
            "startedUtc",
            "expiresUtc",
            "durationMilliseconds",
            "remainingMilliseconds",
            "canUserRemove",
            "recoveryRequired",
            "error",
        ];
        if (HasMalformedPropertyType(item, "taskId", JsonValueKind.String)
            || HasMalformedGuid(item, "taskId")
            || HasMalformedPropertyType(item, "normalizedPath", JsonValueKind.String)
            || HasMalformedPropertyType(item, "status", JsonValueKind.String)
            || HasMalformedNullableTimestamp(item, "startedUtc")
            || HasMalformedNullableTimestamp(item, "expiresUtc")
            || HasMalformedInteger(item, "durationMilliseconds", IntegerType.Int64)
            || HasMalformedInteger(item, "remainingMilliseconds", IntegerType.Int64)
            || HasMalformedPropertyType(item, "canUserRemove", JsonValueKind.False, JsonValueKind.True)
            || HasMalformedPropertyType(item, "recoveryRequired", JsonValueKind.False, JsonValueKind.True)
            || HasMalformedPropertyType(item, "error", JsonValueKind.Null, JsonValueKind.Object))
        {
            return new TaskStatusParseResult(null, MalformedError());
        }

        BrokerError? schemaError = ValidateSchema(item, fields, fields, ["startedUtc", "expiresUtc", "error"]);
        if (schemaError is not null)
        {
            return new TaskStatusParseResult(null, schemaError);
        }

        if (!TryParseLockTaskStatus(item.GetProperty("status").GetString()!, out LockTaskStatus status))
        {
            return new TaskStatusParseResult(null, SchemaError("status"));
        }

        long remaining = item.GetProperty("remainingMilliseconds").GetInt64();
        bool canUserRemove = item.GetProperty("canUserRemove").GetBoolean();
        bool recoveryRequired = item.GetProperty("recoveryRequired").GetBoolean();
        if (remaining < 0
            || canUserRemove
            || recoveryRequired != (status == LockTaskStatus.RecoveryRequired)
            || (status == LockTaskStatus.Completed && remaining != 0))
        {
            return new TaskStatusParseResult(null, SchemaError(null));
        }

        TaskStatusError? error = null;
        JsonElement errorElement = item.GetProperty("error");
        if (errorElement.ValueKind == JsonValueKind.Object)
        {
            TaskStatusErrorParseResult parsedError = ParseTaskStatusError(errorElement);
            if (parsedError.Error is not null)
            {
                return new TaskStatusParseResult(null, parsedError.Error);
            }

            error = parsedError.Value;
        }

        return new TaskStatusParseResult(new TaskStatusItem(
            ParseGuid(item.GetProperty("taskId").GetString()!),
            item.GetProperty("normalizedPath").GetString()!,
            status,
            ParseNullableTimestamp(item.GetProperty("startedUtc")),
            ParseNullableTimestamp(item.GetProperty("expiresUtc")),
            item.GetProperty("durationMilliseconds").GetInt64(),
            remaining,
            false,
            recoveryRequired,
            error), null);
    }

    private static BrokerErrorParseResult ParseBrokerError(JsonElement element)
    {
        HashSet<string> fields = ["code", "message", "retryable", "field"];
        if (HasMalformedPropertyType(element, "code", JsonValueKind.String)
            || HasMalformedPropertyType(element, "message", JsonValueKind.String)
            || HasMalformedPropertyType(element, "retryable", JsonValueKind.False, JsonValueKind.True)
            || HasMalformedPropertyType(element, "field", JsonValueKind.String, JsonValueKind.Null))
        {
            return new BrokerErrorParseResult(null, MalformedError());
        }

        BrokerError? schemaError = ValidateSchema(element, fields, fields, ["field"]);
        if (schemaError is not null)
        {
            return new BrokerErrorParseResult(null, schemaError);
        }

        string code = element.GetProperty("code").GetString()!;
        string message = element.GetProperty("message").GetString()!;
        if (!BrokerProtocolValidation.IsErrorCode(code))
        {
            return new BrokerErrorParseResult(null, SchemaError("code"));
        }

        if (message.Length > BrokerProtocolConstants.MaximumErrorMessageLength)
        {
            return new BrokerErrorParseResult(null, SchemaError("message"));
        }

        return new BrokerErrorParseResult(new BrokerError(
            code,
            message,
            element.GetProperty("retryable").GetBoolean(),
            element.GetProperty("field").ValueKind == JsonValueKind.Null
                ? null
                : element.GetProperty("field").GetString()), null);
    }

    private static TaskStatusErrorParseResult ParseTaskStatusError(JsonElement element)
    {
        HashSet<string> fields = ["code", "message", "retryable"];
        if (HasMalformedPropertyType(element, "code", JsonValueKind.String)
            || HasMalformedPropertyType(element, "message", JsonValueKind.String)
            || HasMalformedPropertyType(element, "retryable", JsonValueKind.False, JsonValueKind.True))
        {
            return new TaskStatusErrorParseResult(null, MalformedError());
        }

        BrokerError? schemaError = ValidateSchema(element, fields, fields);
        if (schemaError is not null)
        {
            return new TaskStatusErrorParseResult(null, schemaError);
        }

        string code = element.GetProperty("code").GetString()!;
        string message = element.GetProperty("message").GetString()!;
        if (!BrokerProtocolValidation.IsErrorCode(code))
        {
            return new TaskStatusErrorParseResult(null, SchemaError("code"));
        }

        if (message.Length > BrokerProtocolConstants.MaximumErrorMessageLength)
        {
            return new TaskStatusErrorParseResult(null, SchemaError("message"));
        }

        return new TaskStatusErrorParseResult(new TaskStatusError(
            code,
            message,
            element.GetProperty("retryable").GetBoolean()), null);
    }

    private static bool HasMalformedEnvelopeType(
        JsonElement root,
        out Guid requestId,
        out BrokerCommand? command)
    {
        requestId = Guid.Empty;
        command = null;
        if (HasMalformedInteger(root, "protocolVersion", IntegerType.Int32)
            || HasMalformedPropertyType(root, "requestId", JsonValueKind.String)
            || HasMalformedPropertyType(root, "command", JsonValueKind.String)
            || HasMalformedInteger(root, "clientSessionId", IntegerType.UInt32)
            || HasMalformedPropertyType(root, "sentAtUtc", JsonValueKind.String)
            || HasMalformedPropertyType(root, "payload", JsonValueKind.Object))
        {
            return true;
        }

        if (root.TryGetProperty("requestId", out JsonElement requestIdElement)
            && requestIdElement.ValueKind == JsonValueKind.String)
        {
            if (!TryParseGuid(requestIdElement.GetString()!, out requestId))
            {
                return true;
            }
        }

        if (root.TryGetProperty("sentAtUtc", out JsonElement sentAtElement)
            && sentAtElement.ValueKind == JsonValueKind.String
            && !TryParseTimestamp(sentAtElement.GetString()!, out _))
        {
            return true;
        }

        if (root.TryGetProperty("command", out JsonElement commandElement)
            && commandElement.ValueKind == JsonValueKind.String
            && TryParseCommand(commandElement.GetString()!, out BrokerCommand parsedCommand))
        {
            command = parsedCommand;
        }

        return false;
    }

    private static bool HasMalformedResponseType(JsonElement root)
    {
        if (HasMalformedInteger(root, "protocolVersion", IntegerType.Int32)
            || HasMalformedNullableGuid(root, "requestId")
            || HasMalformedPropertyType(root, "command", JsonValueKind.String, JsonValueKind.Null)
            || HasMalformedPropertyType(root, "success", JsonValueKind.False, JsonValueKind.True)
            || HasMalformedPropertyType(root, "serverTimeUtc", JsonValueKind.String)
            || HasMalformedPropertyType(root, "result", JsonValueKind.Object, JsonValueKind.Null)
            || HasMalformedPropertyType(root, "error", JsonValueKind.Object, JsonValueKind.Null))
        {
            return true;
        }

        return root.TryGetProperty("serverTimeUtc", out JsonElement serverTimeElement)
            && serverTimeElement.ValueKind == JsonValueKind.String
            && !TryParseTimestamp(serverTimeElement.GetString()!, out _);
    }

    private static BrokerError? ValidateSchema(
        JsonElement element,
        HashSet<string> allowed,
        HashSet<string> required,
        HashSet<string>? nullable = null)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                return SchemaError(property.Name);
            }

            if (property.Value.ValueKind == JsonValueKind.Null
                && (nullable is null || !nullable.Contains(property.Name)))
            {
                return SchemaError(property.Name);
            }
        }

        foreach (string name in required)
        {
            if (!element.TryGetProperty(name, out _))
            {
                return SchemaError(name);
            }
        }

        return null;
    }

    private static bool HasMalformedPropertyType(
        JsonElement element,
        string name,
        params JsonValueKind[] allowedKinds)
    {
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return !allowedKinds.Contains(property.ValueKind);
    }

    private static bool HasMalformedProperties(
        JsonElement element,
        IEnumerable<string> names,
        JsonValueKind expectedKind,
        HashSet<string> excluded) => names
        .Where(name => !excluded.Contains(name))
        .Any(name => HasMalformedPropertyType(element, name, expectedKind));

    private static bool HasMalformedNullableGuid(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return property.ValueKind != JsonValueKind.String
            || !TryParseGuid(property.GetString()!, out _);
    }

    private static bool HasMalformedGuid(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return !TryParseGuid(property.GetString()!, out _);
    }

    private static bool HasMalformedNullableTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return property.ValueKind != JsonValueKind.String
            || !TryParseTimestamp(property.GetString()!, out _);
    }

    private static bool HasMalformedTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return !TryParseTimestamp(property.GetString()!, out _);
    }

    private static bool HasMalformedInteger(
        JsonElement element,
        string name,
        IntegerType integerType)
    {
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.Number)
        {
            return true;
        }

        string raw = property.GetRawText();
        if (raw.IndexOfAny(['.', 'e', 'E', '+']) >= 0)
        {
            return true;
        }

        return integerType switch
        {
            IntegerType.Int32 => !property.TryGetInt32(out _),
            IntegerType.UInt32 => !property.TryGetUInt32(out _),
            IntegerType.Int64 => !property.TryGetInt64(out _),
            _ => true,
        };
    }

    private static bool TryParseDocument(
        ReadOnlyMemory<byte> utf8Json,
        out JsonDocument? document)
    {
        document = null;
        try
        {
            var reader = new Utf8JsonReader(utf8Json.Span, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var objectProperties = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                }
                else if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (objectProperties.Count == 0
                        || !objectProperties.Peek().Add(reader.GetString()!))
                    {
                        return false;
                    }
                }
                else if (reader.TokenType == JsonTokenType.EndObject)
                {
                    objectProperties.Pop();
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

    private static bool TryFindForbiddenEnvelopeField(JsonElement element, out string? field)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name != "payload" && ForbiddenFields.Contains(property.Name))
            {
                field = property.Name;
                return true;
            }
        }

        field = null;
        return false;
    }

    private static bool TryFindForbiddenField(
        JsonElement element,
        HashSet<string> forbidden,
        out string? field,
        string? currentPath)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string propertyPath = currentPath is null
                    ? property.Name
                    : $"{currentPath}.{property.Name}";
                if (forbidden.Contains(property.Name))
                {
                    field = propertyPath;
                    return true;
                }

                if (TryFindForbiddenField(property.Value, forbidden, out field, propertyPath))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (TryFindForbiddenField(item, forbidden, out field, currentPath))
                {
                    return true;
                }
            }
        }

        field = null;
        return false;
    }

    private static BrokerError? ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new BrokerError(
                BrokerErrorCodes.FSL_E_PATH_EMPTY,
                "A folder path is required.",
                false,
                "payload.path");
        }

        if (path.Length > BrokerProtocolConstants.MaximumPathLength)
        {
            return new BrokerError(
                BrokerErrorCodes.FSL_E_PATH_INVALID,
                "The folder path is invalid.",
                false,
                "payload.path");
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal))
        {
            return new BrokerError(
                BrokerErrorCodes.FSL_E_PATH_INVALID,
                "The folder path is invalid.",
                false,
                "payload.path");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new BrokerError(
                BrokerErrorCodes.FSL_E_PATH_NETWORK_FORBIDDEN,
                "Network folder paths are not supported.",
                false,
                "payload.path");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return new BrokerError(
                BrokerErrorCodes.FSL_E_PATH_NOT_ABSOLUTE,
                "The folder path must be absolute.",
                false,
                "payload.path");
        }

        if (path.IndexOf(':', 2) >= 0)
        {
            return new BrokerError(
                BrokerErrorCodes.FSL_E_PATH_INVALID,
                "The folder path is invalid.",
                false,
                "payload.path");
        }

        return null;
    }

    private static void WritePayload(Utf8JsonWriter writer, IBrokerRequestPayload payload)
    {
        writer.WriteStartObject();
        switch (payload)
        {
            case ValidatePathRequest request:
                writer.WriteString("path", request.Path);
                break;
            case CreateLockRequest request:
                if (request.DurationMilliseconds <= 0)
                {
                    throw new ArgumentException("The request duration must be positive.", nameof(payload));
                }

                writer.WriteString("taskId", FormatGuid(request.TaskId));
                writer.WriteString("path", request.Path);
                writer.WriteNumber("durationMilliseconds", request.DurationMilliseconds);
                break;
            case RemoveLockRequest request:
                writer.WriteString("taskId", FormatGuid(request.TaskId));
                writer.WriteString("recoveryRecordId", FormatGuid(request.RecoveryRecordId));
                break;
            case GetStatusRequest request:
                if (!Enum.IsDefined(request.QueryType)
                    || (request.QueryType == GetStatusQueryType.ByTaskId && request.TaskId is null)
                    || (request.QueryType == GetStatusQueryType.CurrentSession && request.TaskId is not null))
                {
                    throw new ArgumentException("The status query does not match the protocol schema.", nameof(payload));
                }

                writer.WriteString("queryType", request.QueryType.ToString());
                WriteNullableGuid(writer, "taskId", request.TaskId);
                break;
            default:
                throw new ArgumentException("The request payload type is not part of protocol v1.", nameof(payload));
        }

        writer.WriteEndObject();
    }

    private static void WriteResult(Utf8JsonWriter writer, IBrokerResult result)
    {
        writer.WriteStartObject();
        switch (result)
        {
            case ValidatePathResult value:
                if (value.VolumeSerialNumber.Length != 16
                    || value.VolumeSerialNumber.Any(character => !IsLowerHex(character))
                    || !IsCanonicalUInt64Decimal(value.FileIdHigh)
                    || !IsCanonicalUInt64Decimal(value.FileIdLow)
                    || value.FileSystem != "NTFS"
                    || value.DriveType != "Fixed"
                    || value.IsReparsePoint
                    || !value.IsAllowed)
                {
                    throw new ArgumentException("The path validation result does not match the protocol schema.", nameof(result));
                }

                writer.WriteString("normalizedPath", value.NormalizedPath);
                writer.WriteString("volumeRoot", value.VolumeRoot);
                writer.WriteString("volumeSerialNumber", value.VolumeSerialNumber);
                writer.WriteString("fileIdHigh", value.FileIdHigh);
                writer.WriteString("fileIdLow", value.FileIdLow);
                writer.WriteString("fileSystem", value.FileSystem);
                writer.WriteString("driveType", value.DriveType);
                writer.WriteBoolean("isReparsePoint", value.IsReparsePoint);
                writer.WriteBoolean("isAllowed", value.IsAllowed);
                break;
            case CreateLockResult value:
                if (value.Status != LockTaskStatus.Active
                    || value.DurationMilliseconds <= 0
                    || value.RemainingMilliseconds < 0)
                {
                    throw new ArgumentException("The create-lock result does not match the protocol schema.", nameof(result));
                }

                writer.WriteString("taskId", FormatGuid(value.TaskId));
                writer.WriteString("normalizedPath", value.NormalizedPath);
                writer.WriteString("status", value.Status.ToString());
                writer.WriteString("startedUtc", FormatTimestamp(value.StartedUtc));
                writer.WriteString("expiresUtc", FormatTimestamp(value.ExpiresUtc));
                writer.WriteNumber("durationMilliseconds", value.DurationMilliseconds);
                writer.WriteNumber("remainingMilliseconds", value.RemainingMilliseconds);
                writer.WriteString("recoveryRecordId", FormatGuid(value.RecoveryRecordId));
                writer.WriteBoolean("idempotentReplay", value.IdempotentReplay);
                break;
            case RemoveLockResult value:
                if (value.RemovalIntent is not (
                        LockRemovalIntent.Expiration
                        or LockRemovalIntent.Recovery
                        or LockRemovalIntent.TestCleanup)
                    || !Enum.IsDefined(value.PreviousStatus)
                    || value.Status != LockTaskStatus.Completed)
                {
                    throw new ArgumentException("The remove-lock result does not match the protocol schema.", nameof(result));
                }

                writer.WriteString("taskId", FormatGuid(value.TaskId));
                writer.WriteString("recoveryRecordId", FormatGuid(value.RecoveryRecordId));
                writer.WriteString("removalIntent", value.RemovalIntent.ToString());
                writer.WriteString("previousStatus", value.PreviousStatus.ToString());
                writer.WriteString("status", value.Status.ToString());
                writer.WriteString("removedUtc", FormatTimestamp(value.RemovedUtc));
                writer.WriteBoolean("aceRemoved", value.AceRemoved);
                writer.WriteBoolean("recoveryRecordDeleted", value.RecoveryRecordDeleted);
                writer.WriteBoolean("idempotentReplay", value.IdempotentReplay);
                break;
            case GetStatusResult value:
                if (!Enum.IsDefined(value.QueryType))
                {
                    throw new ArgumentException("The status result does not match the protocol schema.", nameof(result));
                }

                writer.WriteString("queryType", value.QueryType.ToString());
                writer.WritePropertyName("tasks");
                writer.WriteStartArray();
                foreach (TaskStatusItem task in value.Tasks)
                {
                    WriteTaskStatusItem(writer, task);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentException("The response result type is not part of protocol v1.", nameof(result));
        }

        writer.WriteEndObject();
    }

    private static void WriteTaskStatusItem(Utf8JsonWriter writer, TaskStatusItem task)
    {
        if (!Enum.IsDefined(task.Status)
            || task.DurationMilliseconds <= 0
            || task.RemainingMilliseconds < 0
            || task.CanUserRemove
            || task.RecoveryRequired != (task.Status == LockTaskStatus.RecoveryRequired)
            || (task.Status == LockTaskStatus.Completed && task.RemainingMilliseconds != 0))
        {
            throw new ArgumentException("The task status does not match the protocol schema.", nameof(task));
        }

        writer.WriteStartObject();
        writer.WriteString("taskId", FormatGuid(task.TaskId));
        writer.WriteString("normalizedPath", task.NormalizedPath);
        writer.WriteString("status", task.Status.ToString());
        WriteNullableTimestamp(writer, "startedUtc", task.StartedUtc);
        WriteNullableTimestamp(writer, "expiresUtc", task.ExpiresUtc);
        writer.WriteNumber("durationMilliseconds", task.DurationMilliseconds);
        writer.WriteNumber("remainingMilliseconds", task.RemainingMilliseconds);
        writer.WriteBoolean("canUserRemove", task.CanUserRemove);
        writer.WriteBoolean("recoveryRequired", task.RecoveryRequired);
        writer.WritePropertyName("error");
        if (task.Error is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("code", task.Error.Code);
            writer.WriteString("message", task.Error.Message);
            writer.WriteBoolean("retryable", task.Error.Retryable);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
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

    private static BrokerRequestParseResult Failure(
        Guid requestId,
        BrokerCommand? command,
        DateTimeOffset serverTimeUtc,
        string code,
        string message,
        string? field) => Failure(
            requestId == Guid.Empty ? null : requestId,
            command,
            serverTimeUtc,
            new BrokerError(code, message, false, PrefixPayloadPath(field)));

    private static BrokerRequestParseResult Failure(
        Guid requestId,
        BrokerCommand? command,
        DateTimeOffset serverTimeUtc,
        BrokerError error) => Failure(
            requestId == Guid.Empty ? null : requestId,
            command,
            serverTimeUtc,
            error);

    private static BrokerRequestParseResult Failure(
        Guid? requestId,
        BrokerCommand? command,
        DateTimeOffset serverTimeUtc,
        BrokerError error) => BrokerRequestParseResult.Failure(
            BrokerResponseEnvelope.Failed(requestId, command, serverTimeUtc, error));

    private static PayloadParseResult MalformedPayload() =>
        new(null, MalformedError());

    private static ResultParseResult MalformedResult() =>
        new(null, MalformedError());

    private static PayloadParseResult ForbiddenPayload(string? field) => new(
        null,
        new BrokerError(
            BrokerErrorCodes.FSL_E_FORBIDDEN_INPUT,
            "The request contains an input that clients are not allowed to control.",
            false,
            field));

    private static BrokerError MalformedError() => new(
        BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE,
        "The request message is malformed.",
        false,
        null);

    private static BrokerError SchemaError(string? field) => new(
        BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION,
        "The request does not match the protocol schema.",
        false,
        field);

    private static BrokerError PrefixPayloadField(BrokerError error) => error with
    {
        Field = PrefixPayloadPath(error.Field),
    };

    private static string? PrefixPayloadPath(string? field) => field is null
        ? null
        : field.StartsWith("payload.", StringComparison.Ordinal) || EnvelopeFields.Contains(field)
            ? field
            : $"payload.{field}";

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

    private static string CommandName(BrokerCommand command) => command switch
    {
        BrokerCommand.ValidatePath => BrokerProtocolConstants.ValidatePath,
        BrokerCommand.CreateLock => BrokerProtocolConstants.CreateLock,
        BrokerCommand.RemoveLock => BrokerProtocolConstants.RemoveLock,
        BrokerCommand.GetStatus => BrokerProtocolConstants.GetStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private static bool PayloadMatchesCommand(BrokerCommand command, IBrokerRequestPayload payload) =>
        (command, payload) switch
        {
            (BrokerCommand.ValidatePath, ValidatePathRequest) => true,
            (BrokerCommand.CreateLock, CreateLockRequest) => true,
            (BrokerCommand.RemoveLock, RemoveLockRequest) => true,
            (BrokerCommand.GetStatus, GetStatusRequest) => true,
            _ => false,
        };

    private static bool TryParseGuid(string value, out Guid guid) =>
        Guid.TryParseExact(value, BrokerProtocolConstants.GuidFormat, out guid)
        && guid != Guid.Empty
        && string.Equals(value, guid.ToString("D"), StringComparison.Ordinal);

    private static Guid ParseGuid(string value) => TryParseGuid(value, out Guid guid)
        ? guid
        : throw new JsonException("The Guid value does not use the protocol format.");

    private static string FormatGuid(Guid guid)
    {
        if (guid == Guid.Empty)
        {
            throw new ArgumentException("Protocol Guid values cannot be empty.", nameof(guid));
        }

        return guid.ToString("D");
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(
            value,
            BrokerProtocolConstants.UtcTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp)
        && timestamp.Offset == TimeSpan.Zero;

    private static DateTimeOffset ParseTimestamp(string value) =>
        TryParseTimestamp(value, out DateTimeOffset timestamp)
            ? timestamp
            : throw new JsonException("The timestamp does not use the protocol format.");

    private static DateTimeOffset? ParseNullableTimestamp(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null
            ? null
            : ParseTimestamp(element.GetString()!);

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString(
            BrokerProtocolConstants.UtcTimestampFormat,
            CultureInfo.InvariantCulture);

    private static bool TryParseRemovalIntent(string value, out LockRemovalIntent intent)
    {
        intent = value switch
        {
            "Expiration" => LockRemovalIntent.Expiration,
            "Recovery" => LockRemovalIntent.Recovery,
            "TestCleanup" => LockRemovalIntent.TestCleanup,
            _ => (LockRemovalIntent)(-1),
        };
        return Enum.IsDefined(intent);
    }

    private static bool TryParseLockTaskStatus(string value, out LockTaskStatus status)
    {
        status = value switch
        {
            "Created" => LockTaskStatus.Created,
            "Activating" => LockTaskStatus.Activating,
            "Active" => LockTaskStatus.Active,
            "Unlocking" => LockTaskStatus.Unlocking,
            "Completed" => LockTaskStatus.Completed,
            "ActivationFailed" => LockTaskStatus.ActivationFailed,
            "UnlockFailed" => LockTaskStatus.UnlockFailed,
            "RecoveryRequired" => LockTaskStatus.RecoveryRequired,
            _ => (LockTaskStatus)(-1),
        };
        return Enum.IsDefined(status);
    }

    private static bool TryParseQueryType(string value, out GetStatusQueryType queryType)
    {
        queryType = value switch
        {
            "ByTaskId" => GetStatusQueryType.ByTaskId,
            "CurrentSession" => GetStatusQueryType.CurrentSession,
            _ => (GetStatusQueryType)(-1),
        };
        return Enum.IsDefined(queryType);
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsCanonicalUInt64Decimal(string value) =>
        value.Length > 0
        && (value == "0" || (value[0] is >= '1' and <= '9' && value.All(char.IsAsciiDigit)))
        && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

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

    private enum IntegerType
    {
        Int32,
        UInt32,
        Int64,
    }

    private sealed record PayloadParseResult(IBrokerRequestPayload? Payload, BrokerError? Error);

    private sealed record ResultParseResult(IBrokerResult? Value, BrokerError? Error);

    private sealed record TaskStatusParseResult(TaskStatusItem? Value, BrokerError? Error);

    private sealed record BrokerErrorParseResult(BrokerError? Value, BrokerError? Error);

    private sealed record TaskStatusErrorParseResult(TaskStatusError? Value, BrokerError? Error);
}
