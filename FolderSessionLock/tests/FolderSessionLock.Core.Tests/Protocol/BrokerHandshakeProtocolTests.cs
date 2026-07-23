using System.Text;
using System.Text.Json;

namespace FolderSessionLock.Protocol.Tests;

public sealed class BrokerHandshakeProtocolTests
{
    private static readonly string ClientNonce = BrokerHandshakeBinding.CreateNonce();

    [Fact]
    public void ClientHello_SerializesExactlyNineFieldsAndRoundTrips()
    {
        BrokerClientHello hello = CreateClientHello();

        byte[] json = BrokerHandshakeProtocolJson.SerializeFrame(hello);
        using JsonDocument document = JsonDocument.Parse(json);
        BrokerHandshakeFrameParseResult parsed = BrokerHandshakeProtocolJson.DeserializeFrame(
            json,
            ProtocolTestData.ServerTimeUtc,
            ProtocolTestData.DurationPolicy);

        Assert.Equal(
            ["frameType",
                "handshakeVersion",
                "protocolVersion",
                "requestId",
                "command",
                "claimedClientProcessId",
                "clientSessionId",
                "clientNonce",
                "sentAtUtc"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(parsed.IsSuccess);
        Assert.Equal(hello, Assert.IsType<BrokerClientHello>(parsed.Frame));
    }

    [Fact]
    public void ServerHello_SuccessAndFailureUseExactNullInvariants()
    {
        BrokerServerHello success = BrokerServerHello.Succeeded(
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            ProtocolTestData.ServerTimeUtc,
            Guid.ParseExact("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "D"),
            BrokerHandshakeBinding.CreateNonce());
        BrokerServerHello failure = BrokerServerHello.Failed(
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            ProtocolTestData.ServerTimeUtc,
            new BrokerError(
                BrokerErrorCodes.FSL_E_HANDSHAKE_REQUIRED,
                "A valid handshake is required.",
                true,
                "frameType"));

        using JsonDocument successJson = JsonDocument.Parse(BrokerHandshakeProtocolJson.SerializeFrame(success));
        using JsonDocument failureJson = JsonDocument.Parse(BrokerHandshakeProtocolJson.SerializeFrame(failure));

        Assert.Equal(9, successJson.RootElement.EnumerateObject().Count());
        Assert.Equal(["connectionId", "serverNonce", "expiresUtc"],
            successJson.RootElement.GetProperty("result").EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, successJson.RootElement.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Null, failureJson.RootElement.GetProperty("result").ValueKind);
        Assert.Equal(4, failureJson.RootElement.GetProperty("error").EnumerateObject().Count());
    }

    [Fact]
    public void CommandFrames_WrapExistingRequestAndResponseContracts()
    {
        var request = new BrokerRequestEnvelope(
            1,
            ProtocolTestData.RequestId,
            BrokerCommand.GetStatus,
            1,
            ProtocolTestData.ServerTimeUtc,
            new GetStatusRequest(GetStatusQueryType.CurrentSession, null));
        Guid connectionId = Guid.ParseExact("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "D");
        var commandRequest = new BrokerCommandRequest(
            BrokerFrameType.CommandRequest,
            1,
            1,
            request.RequestId,
            request.Command,
            connectionId,
            BrokerHandshakeBinding.CreateProof(
                request.RequestId,
                request.Command,
                connectionId,
                ClientNonce,
                BrokerHandshakeBinding.CreateNonce(),
                request.ClientSessionId),
            request);
        var response = new BrokerCommandResponse(
            BrokerFrameType.CommandResponse,
            1,
            1,
            request.RequestId,
            request.Command,
            connectionId,
            BrokerResponseEnvelope.Succeeded(
                request.RequestId,
                request.Command,
                ProtocolTestData.ServerTimeUtc,
                new GetStatusResult(GetStatusQueryType.CurrentSession, [])));

        BrokerHandshakeFrameParseResult parsedRequest = Parse(
            BrokerHandshakeProtocolJson.SerializeFrame(commandRequest));
        BrokerHandshakeFrameParseResult parsedResponse = Parse(
            BrokerHandshakeProtocolJson.SerializeFrame(response));

        Assert.IsType<BrokerCommandRequest>(parsedRequest.Frame);
        Assert.IsType<BrokerCommandResponse>(parsedResponse.Frame);
    }

    [Fact]
    public void Parser_RejectsDuplicateUnknownMissingAndInvalidNonce()
    {
        string valid = Encoding.UTF8.GetString(BrokerHandshakeProtocolJson.SerializeFrame(CreateClientHello()));
        string duplicate = valid.Replace(
            "\"frameType\":\"ClientHello\"",
            "\"frameType\":\"ClientHello\",\"frameType\":\"ClientHello\"",
            StringComparison.Ordinal);
        string unknown = valid.Replace(
            "\"sentAtUtc\"",
            "\"extra\":1,\"sentAtUtc\"",
            StringComparison.Ordinal);
        string missing = valid.Replace(
            $",\"clientNonce\":\"{ClientNonce}\"",
            string.Empty,
            StringComparison.Ordinal);
        string invalidNonce = valid.Replace(ClientNonce, "AAAAAAAA", StringComparison.Ordinal);

        Assert.Equal(BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE, Parse(duplicate).Error!.Code);
        Assert.Equal(BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION, Parse(unknown).Error!.Code);
        Assert.Equal(BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION, Parse(missing).Error!.Code);
        Assert.Equal(BrokerErrorCodes.FSL_E_MALFORMED_MESSAGE, Parse(invalidNonce).Error!.Code);
    }

    [Fact]
    public void UnsupportedHandshakeVersionUsesFixedError()
    {
        string json = Encoding.UTF8.GetString(BrokerHandshakeProtocolJson.SerializeFrame(CreateClientHello()))
            .Replace("\"handshakeVersion\":1", "\"handshakeVersion\":2", StringComparison.Ordinal);

        BrokerHandshakeFrameParseResult parsed = Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(BrokerErrorCodes.FSL_E_HANDSHAKE_VERSION_UNSUPPORTED, parsed.Error!.Code);
        Assert.False(parsed.Error.Retryable);
        Assert.Equal("handshakeVersion", parsed.Error.Field);
    }

    [Fact]
    public void BindingProofUsesExactCanonicalInputAndFixedTimeVerification()
    {
        Guid connectionId = Guid.ParseExact("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "D");
        string serverNonce = BrokerHandshakeBinding.CreateNonce();
        string proof = BrokerHandshakeBinding.CreateProof(
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            connectionId,
            ClientNonce,
            serverNonce,
            1);

        Assert.True(BrokerHandshakeBinding.VerifyProof(
            proof,
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            connectionId,
            ClientNonce,
            serverNonce,
            1));
        Assert.False(BrokerHandshakeBinding.VerifyProof(
            proof,
            ProtocolTestData.RequestId,
            BrokerCommand.ValidatePath,
            connectionId,
            ClientNonce,
            serverNonce,
            2));
        Assert.DoesNotContain('=', proof);
        Assert.Equal(32, Decode(proof).Length);
    }

    [Fact]
    public void NoncesAreBase64UrlThirtyTwoBytesNonZeroAndUnique()
    {
        string first = BrokerHandshakeBinding.CreateNonce();
        string second = BrokerHandshakeBinding.CreateNonce();

        Assert.True(BrokerHandshakeBinding.IsValidNonce(first));
        Assert.True(BrokerHandshakeBinding.IsValidNonce(second));
        Assert.NotEqual(first, second);
        Assert.Equal(32, Decode(first).Length);
        Assert.DoesNotContain('=', first);
    }

    private static BrokerClientHello CreateClientHello() => new(
        BrokerFrameType.ClientHello,
        1,
        1,
        ProtocolTestData.RequestId,
        BrokerCommand.ValidatePath,
        123,
        1,
        ClientNonce,
        ProtocolTestData.ServerTimeUtc);

    private static BrokerHandshakeFrameParseResult Parse(ReadOnlyMemory<byte> json) =>
        BrokerHandshakeProtocolJson.DeserializeFrame(
            json,
            ProtocolTestData.ServerTimeUtc,
            ProtocolTestData.DurationPolicy);

    private static BrokerHandshakeFrameParseResult Parse(string json) =>
        Parse(Encoding.UTF8.GetBytes(json));

    private static byte[] Decode(string value)
    {
        Assert.True(BrokerHandshakeBinding.TryBase64UrlDecode(value, out byte[] bytes));
        return bytes;
    }
}
