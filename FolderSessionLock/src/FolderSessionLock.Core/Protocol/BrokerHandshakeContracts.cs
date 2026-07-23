using System.Text.Json.Serialization;

namespace FolderSessionLock.Protocol;

public enum BrokerFrameType
{
    ClientHello,
    ServerHello,
    CommandRequest,
    CommandResponse,
}

public sealed record BrokerClientHello(
    [property: JsonPropertyName("frameType")] BrokerFrameType FrameType,
    [property: JsonPropertyName("handshakeVersion")] int HandshakeVersion,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("command")] BrokerCommand Command,
    [property: JsonPropertyName("claimedClientProcessId")] uint ClaimedClientProcessId,
    [property: JsonPropertyName("clientSessionId")] uint ClientSessionId,
    [property: JsonPropertyName("clientNonce")] string ClientNonce,
    [property: JsonPropertyName("sentAtUtc")] DateTimeOffset SentAtUtc);

public sealed record BrokerServerHelloResult(
    [property: JsonPropertyName("connectionId")] Guid ConnectionId,
    [property: JsonPropertyName("serverNonce")] string ServerNonce,
    [property: JsonPropertyName("expiresUtc")] DateTimeOffset ExpiresUtc);

public sealed record BrokerServerHello(
    [property: JsonPropertyName("frameType")] BrokerFrameType FrameType,
    [property: JsonPropertyName("handshakeVersion")] int HandshakeVersion,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("requestId")] Guid? RequestId,
    [property: JsonPropertyName("command")] string? Command,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("serverTimeUtc")] DateTimeOffset ServerTimeUtc,
    [property: JsonPropertyName("result")] BrokerServerHelloResult? Result,
    [property: JsonPropertyName("error")] BrokerError? Error)
{
    public static BrokerServerHello Succeeded(
        Guid requestId,
        BrokerCommand command,
        DateTimeOffset serverTimeUtc,
        Guid connectionId,
        string serverNonce) => new(
            BrokerFrameType.ServerHello,
            BrokerProtocolConstants.HandshakeVersion,
            BrokerProtocolConstants.ProtocolVersion,
            requestId,
            command.ToString(),
            true,
            serverTimeUtc.ToUniversalTime(),
            new BrokerServerHelloResult(
                connectionId,
                serverNonce,
                serverTimeUtc.ToUniversalTime().AddSeconds(30)),
            null);

    public static BrokerServerHello Failed(
        Guid? requestId,
        BrokerCommand? command,
        DateTimeOffset serverTimeUtc,
        BrokerError error) => new(
            BrokerFrameType.ServerHello,
            BrokerProtocolConstants.HandshakeVersion,
            BrokerProtocolConstants.ProtocolVersion,
            requestId,
            command?.ToString(),
            false,
            serverTimeUtc.ToUniversalTime(),
            null,
            error);
}

public sealed record BrokerCommandRequest(
    [property: JsonPropertyName("frameType")] BrokerFrameType FrameType,
    [property: JsonPropertyName("handshakeVersion")] int HandshakeVersion,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("command")] BrokerCommand Command,
    [property: JsonPropertyName("connectionId")] Guid ConnectionId,
    [property: JsonPropertyName("bindingProof")] string BindingProof,
    [property: JsonPropertyName("request")] BrokerRequestEnvelope Request);

public sealed record BrokerCommandResponse(
    [property: JsonPropertyName("frameType")] BrokerFrameType FrameType,
    [property: JsonPropertyName("handshakeVersion")] int HandshakeVersion,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("command")] BrokerCommand Command,
    [property: JsonPropertyName("connectionId")] Guid ConnectionId,
    [property: JsonPropertyName("response")] BrokerResponseEnvelope Response);

public sealed record BrokerHandshakeFrameParseResult(
    object? Frame,
    BrokerError? Error,
    Guid? RequestId,
    BrokerCommand? Command)
{
    public bool IsSuccess => Frame is not null;

    public static BrokerHandshakeFrameParseResult Success(object frame) => new(frame, null, null, null);

    public static BrokerHandshakeFrameParseResult Failure(
        BrokerError error,
        Guid? requestId,
        BrokerCommand? command) => new(null, error, requestId, command);
}
