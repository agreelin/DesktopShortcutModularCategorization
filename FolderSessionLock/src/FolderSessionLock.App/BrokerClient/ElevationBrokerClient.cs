using System.Buffers.Binary;
using System.IO;
using System.Text;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.App.BrokerClient;

internal sealed record BrokerClientResult(
    BrokerResponseEnvelope? Response,
    BrokerError? Error)
{
    internal bool IsSuccess => Response is not null;
}

internal sealed class ElevationBrokerClient
{
    private static readonly TimeSpan ServerHelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandResponseTimeout = TimeSpan.FromSeconds(30);
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.CreateProduction();
    private readonly IRecoveryReadinessReader _readiness;
    private readonly IInitiatingClientIdentityProvider _identity;
    private readonly IBrokerPathResolver _pathResolver;
    private readonly IConsentElevationLauncher _launcher;
    private readonly BrokerConnectionRace _connectionRace;
    private readonly Func<DateTimeOffset> _utcNow;

    internal ElevationBrokerClient(
        IRecoveryReadinessReader readiness,
        IInitiatingClientIdentityProvider identity,
        IBrokerPathResolver pathResolver,
        IConsentElevationLauncher launcher,
        BrokerConnectionRace connectionRace,
        Func<DateTimeOffset>? utcNow = null)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _connectionRace = connectionRace ?? throw new ArgumentNullException(nameof(connectionRace));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal async ValueTask<BrokerClientResult> ExecuteAsync(
        BrokerRequestEnvelope request,
        nint ownerWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            _ = BrokerProtocolJson.SerializeRequest(request);
        }
        catch (ArgumentException)
        {
            return Failure(new BrokerError(
                BrokerErrorCodes.FSL_E_SCHEMA_VIOLATION,
                "The request message does not match the required schema.",
                false,
                null));
        }

        RecoveryReadinessSnapshot snapshot;
        try
        {
            snapshot = await _readiness.ReadAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is RecoveryReadinessException
                or IOException
                or UnauthorizedAccessException)
        {
            return Failure(RecoveryBlocking());
        }

        if (!RecoveryReadinessPolicy.IsReady(snapshot, _utcNow().ToUniversalTime()))
        {
            return Failure(RecoveryBlocking());
        }

        Result<InitiatingClientIdentity> identity = _identity.Capture();
        if (identity.IsFailure)
        {
            return Failure(ToBrokerError(identity.Error!));
        }

        Result<ResolvedBrokerPath> brokerPath = _pathResolver.Resolve();
        if (brokerPath.IsFailure)
        {
            return Failure(ToBrokerError(brokerPath.Error!));
        }

        Result<IBrokerProcessHandle> launch = await _launcher.LaunchAsync(new(
            brokerPath.Value,
            request.RequestId,
            identity.Value,
            ownerWindow));
        if (launch.IsFailure)
        {
            return Failure(ToBrokerError(launch.Error!));
        }

        BrokerConnectionResult connection = await _connectionRace.ConnectAsync(
            launch.Value,
            cancellationToken);
        if (!connection.IsConnected)
        {
            return Failure(connection.Error!);
        }

        using Stream pipe = connection.Pipe!;
        using IBrokerProcessHandle process = connection.Process!;
        string clientNonce = BrokerHandshakeBinding.CreateNonce();
        var hello = new BrokerClientHello(
            BrokerFrameType.ClientHello,
            BrokerProtocolConstants.HandshakeVersion,
            BrokerProtocolConstants.ProtocolVersion,
            request.RequestId,
            request.Command,
            identity.Value.ProcessId,
            identity.Value.WindowsSessionId,
            clientNonce,
            _utcNow().ToUniversalTime());
        try
        {
            await BrokerClientFrameCodec.WriteAsync(
                pipe,
                BrokerHandshakeProtocolJson.SerializeFrame(hello),
                cancellationToken);
            ReadOnlyMemory<byte> serverBytes = await BrokerClientFrameCodec.ReadAsync(
                pipe,
                ServerHelloTimeout,
                cancellationToken);
            BrokerHandshakeFrameParseResult parsedServer =
                BrokerHandshakeProtocolJson.DeserializeFrame(
                    serverBytes,
                    _utcNow().ToUniversalTime(),
                    DurationPolicy);
            if (!parsedServer.IsSuccess || parsedServer.Frame is not BrokerServerHello server)
            {
                return Failure(parsedServer.Error ?? BrokerExitCodeMapper.ExitedEarly());
            }

            if (!server.Success)
            {
                return Failure(MapAccountMismatch(server.Error!));
            }

            if (server.RequestId != request.RequestId
                || server.Command != request.Command.ToString()
                || server.Result is null)
            {
                return Failure(new BrokerError(
                    BrokerErrorCodes.FSL_E_REQUEST_BINDING_MISMATCH,
                    "The request is not bound to the active handshake.",
                    false,
                    null));
            }

            string proof = BrokerHandshakeBinding.CreateProof(
                request.RequestId,
                request.Command,
                server.Result.ConnectionId,
                clientNonce,
                server.Result.ServerNonce,
                identity.Value.WindowsSessionId);
            var command = new BrokerCommandRequest(
                BrokerFrameType.CommandRequest,
                BrokerProtocolConstants.HandshakeVersion,
                BrokerProtocolConstants.ProtocolVersion,
                request.RequestId,
                request.Command,
                server.Result.ConnectionId,
                proof,
                request);
            await BrokerClientFrameCodec.WriteAsync(
                pipe,
                BrokerHandshakeProtocolJson.SerializeFrame(command),
                cancellationToken);
            TimeSpan responseTimeout = server.Result.ExpiresUtc - _utcNow().ToUniversalTime();
            if (responseTimeout <= TimeSpan.Zero || responseTimeout > CommandResponseTimeout)
            {
                responseTimeout = CommandResponseTimeout;
            }

            ReadOnlyMemory<byte> responseBytes = await BrokerClientFrameCodec.ReadAsync(
                pipe,
                responseTimeout,
                cancellationToken);
            BrokerHandshakeFrameParseResult parsedResponse =
                BrokerHandshakeProtocolJson.DeserializeFrame(
                    responseBytes,
                    _utcNow().ToUniversalTime(),
                    DurationPolicy);
            if (!parsedResponse.IsSuccess
                || parsedResponse.Frame is not BrokerCommandResponse response
                || response.RequestId != request.RequestId
                || response.Command != request.Command
                || response.ConnectionId != server.Result.ConnectionId)
            {
                return Failure(parsedResponse.Error ?? new BrokerError(
                    BrokerErrorCodes.FSL_E_REQUEST_BINDING_MISMATCH,
                    "The request is not bound to the active handshake.",
                    false,
                    null));
            }

            return response.Response.Success
                ? new BrokerClientResult(response.Response, null)
                : Failure(MapAccountMismatch(response.Response.Error!));
        }
        catch (Exception exception) when (
            exception is IOException
                or OperationCanceledException
                or InvalidOperationException
                or ArgumentException)
        {
            return Failure(BrokerExitCodeMapper.ExitedEarly());
        }
    }

    private static BrokerError RecoveryBlocking() => new(
        BrokerErrorCodes.FSL_E_RECOVERY_BLOCKING,
        "Folder restrictions cannot be created until recovery is complete.",
        true,
        null);

    private static BrokerError MapAccountMismatch(BrokerError error) =>
        error.Code == BrokerErrorCodes.FSL_E_ACCOUNT_SID_MISMATCH
            ? new BrokerError(
                BrokerErrorCodes.FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED,
                "Cross-account elevation is not supported.",
                false,
                null)
            : error;

    private static BrokerError ToBrokerError(Error error) => new(
        error.Code,
        error.Message,
        error.Code == BrokerErrorCodes.FSL_E_ELEVATION_CANCELLED,
        null);

    private static BrokerClientResult Failure(BrokerError error) => new(null, error);
}

internal static class BrokerClientFrameCodec
{
    private const int PrefixLength = sizeof(uint);
    private const int MaximumBodyLength = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (body.Length is < 1 or > MaximumBodyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(body));
        }

        byte[] prefix = new byte[PrefixLength];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)body.Length));
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        byte[] prefix = new byte[PrefixLength];
        await stream.ReadExactlyAsync(prefix, timeoutSource.Token);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumBodyLength)
        {
            throw new IOException("The broker frame length is invalid.");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, timeoutSource.Token);
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            throw new IOException("The broker frame encoding is invalid.");
        }

        _ = StrictUtf8.GetString(body);
        return body;
    }
}
