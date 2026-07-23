using System.Text.Json.Serialization;

namespace FolderSessionLock.Protocol;

public enum BrokerCommand
{
    ValidatePath,
    CreateLock,
    RemoveLock,
    GetStatus,
}

public interface IBrokerRequestPayload
{
}

public interface IBrokerResult
{
}

public sealed record BrokerRequestEnvelope(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("command")] BrokerCommand Command,
    [property: JsonPropertyName("clientSessionId")] uint ClientSessionId,
    [property: JsonPropertyName("sentAtUtc")] DateTimeOffset SentAtUtc,
    [property: JsonPropertyName("payload")] IBrokerRequestPayload Payload);

public sealed record BrokerError
{
    public const string InternalMessage = "The operation could not be completed.";

    public BrokerError(string code, string message, bool retryable, string? field)
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
        Field = field;
    }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; }

    [JsonPropertyName("field")]
    public string? Field { get; init; }

    public static BrokerError Internal() => new(
        BrokerErrorCodes.FSL_E_INTERNAL,
        InternalMessage,
        false,
        null);
}

public sealed record BrokerResponseEnvelope
{
    private BrokerResponseEnvelope(
        int protocolVersion,
        Guid? requestId,
        string? command,
        bool success,
        DateTimeOffset serverTimeUtc,
        IBrokerResult? result,
        BrokerError? error)
    {
        if (success == (result is null) || success == (error is not null))
        {
            throw new ArgumentException("Response success, result, and error values are inconsistent.");
        }

        ProtocolVersion = protocolVersion;
        RequestId = requestId;
        Command = command;
        Success = success;
        ServerTimeUtc = serverTimeUtc.ToUniversalTime();
        Result = result;
        Error = error;
    }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; }

    [JsonPropertyName("requestId")]
    public Guid? RequestId { get; }

    [JsonPropertyName("command")]
    public string? Command { get; }

    [JsonPropertyName("success")]
    public bool Success { get; }

    [JsonPropertyName("serverTimeUtc")]
    public DateTimeOffset ServerTimeUtc { get; }

    [JsonPropertyName("result")]
    public IBrokerResult? Result { get; }

    [JsonPropertyName("error")]
    public BrokerError? Error { get; }

    public static BrokerResponseEnvelope Succeeded(
        Guid requestId,
        BrokerCommand command,
        DateTimeOffset serverTimeUtc,
        IBrokerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!IsResultForCommand(command, result))
        {
            throw new ArgumentException("The result type does not match the response command.", nameof(result));
        }

        return new BrokerResponseEnvelope(
            BrokerProtocolConstants.ProtocolVersion,
            requestId,
            command.ToString(),
            true,
            serverTimeUtc,
            result,
            null);
    }

    public static BrokerResponseEnvelope Failed(
        Guid? requestId,
        BrokerCommand? command,
        DateTimeOffset serverTimeUtc,
        BrokerError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (command is not null && !Enum.IsDefined(command.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        return new BrokerResponseEnvelope(
            BrokerProtocolConstants.ProtocolVersion,
            requestId,
            command?.ToString(),
            false,
            serverTimeUtc,
            null,
            error);
    }

    public static BrokerResponseEnvelope Malformed(DateTimeOffset serverTimeUtc) => Failed(
        null,
        null,
        serverTimeUtc,
        new BrokerError(
            BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE,
            "The request message is malformed.",
            false,
            null));

    private static bool IsResultForCommand(BrokerCommand command, IBrokerResult result) =>
        (command, result) switch
        {
            (BrokerCommand.ValidatePath, ValidatePathResult) => true,
            (BrokerCommand.CreateLock, CreateLockResult) => true,
            (BrokerCommand.RemoveLock, RemoveLockResult) => true,
            (BrokerCommand.GetStatus, GetStatusResult) => true,
            _ => false,
        };
}

public sealed record BrokerRequestParseResult(
    BrokerRequestEnvelope? Request,
    BrokerResponseEnvelope? FailureResponse)
{
    public bool IsSuccess => Request is not null;

    public static BrokerRequestParseResult Success(BrokerRequestEnvelope request) =>
        new(request, null);

    public static BrokerRequestParseResult Failure(BrokerResponseEnvelope response) =>
        new(null, response);
}

public sealed record BrokerResponseParseResult(
    BrokerResponseEnvelope? Response,
    BrokerError? Error)
{
    public bool IsSuccess => Response is not null;

    public static BrokerResponseParseResult Success(BrokerResponseEnvelope response) =>
        new(response, null);

    public static BrokerResponseParseResult Failure(BrokerError error) =>
        new(null, error);
}
