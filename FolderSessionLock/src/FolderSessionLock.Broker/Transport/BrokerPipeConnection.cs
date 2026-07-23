using FolderSessionLock.Broker.Security;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Transport;

public sealed record BrokerPipeConnectionResult(
    bool ResponseWritten,
    BrokerError? Error);

public static class BrokerPipeConnection
{
    public static readonly TimeSpan ClientHelloTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestTimeWindow = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MaximumExecutionDuration = TimeSpan.FromMinutes(5);

    public static async ValueTask<BrokerPipeConnectionResult> ProcessAsync(
        Stream stream,
        BrokerConsentOptions options,
        LockDurationPolicy durationPolicy,
        IClock clock,
        IBrokerConnectionAuthenticator authenticator,
        IReplayRegistry replayRegistry,
        Func<BrokerRequestEnvelope, CancellationToken, ValueTask<BrokerExecutionOutcome>> processRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(durationPolicy);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(replayRegistry);
        ArgumentNullException.ThrowIfNull(processRequest);

        BrokerPipeReadResult first = await BrokerPipeFrameCodec.ReadAsync(
            stream,
            ClientHelloTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!first.IsSuccess)
        {
            return cancellationToken.IsCancellationRequested
                ? new BrokerPipeConnectionResult(false, first.Error)
                : await WriteServerFailureAsync(
                    stream,
                    null,
                    null,
                    clock.UtcNow,
                    first.Error!,
                    cancellationToken).ConfigureAwait(false);
        }

        BrokerHandshakeFrameParseResult firstFrame = BrokerHandshakeProtocolJson.DeserializeFrame(
            first.Body,
            clock.UtcNow,
            durationPolicy);
        if (!firstFrame.IsSuccess)
        {
            return await WriteServerFailureAsync(
                stream,
                firstFrame.RequestId,
                firstFrame.Command,
                clock.UtcNow,
                firstFrame.Error!,
                cancellationToken).ConfigureAwait(false);
        }

        if (firstFrame.Frame is not BrokerClientHello hello)
        {
            return await WriteServerFailureAsync(
                stream,
                FrameRequestId(firstFrame.Frame!),
                FrameCommand(firstFrame.Frame!),
                clock.UtcNow,
                Error(
                    BrokerErrorCodes.FSL_E_HANDSHAKE_REQUIRED,
                    "A valid handshake is required.",
                    true,
                    "frameType"),
                cancellationToken).ConfigureAwait(false);
        }

        BrokerError? preAuthenticationError = ValidateClientHello(hello, options, clock.UtcNow);
        if (preAuthenticationError is not null)
        {
            return await WriteServerFailureAsync(
                stream,
                hello.RequestId,
                hello.Command,
                clock.UtcNow,
                preAuthenticationError,
                cancellationToken).ConfigureAwait(false);
        }

        BrokerAuthenticationResult authentication = await authenticator.AuthenticateAsync(
            stream,
            hello,
            options,
            cancellationToken).ConfigureAwait(false);
        if (!authentication.IsSuccess)
        {
            return await WriteServerFailureAsync(
                stream,
                hello.RequestId,
                hello.Command,
                clock.UtcNow,
                authentication.Error!,
                cancellationToken).ConfigureAwait(false);
        }

        BrokerPermissionDecision permission = BrokerPermissionPolicy.Evaluate(
            BrokerExecutionContext.OrdinaryUi,
            hello.Command);
        if (!permission.IsAllowed)
        {
            return await WriteServerFailureAsync(
                stream,
                hello.RequestId,
                hello.Command,
                clock.UtcNow,
                permission.Error!,
                cancellationToken).ConfigureAwait(false);
        }

        ReplayAcquireResult acquisition = await replayRegistry.AcquireAsync(
            authentication.Client!,
            hello.RequestId,
            hello.Command,
            cancellationToken).ConfigureAwait(false);
        if (!acquisition.IsSuccess)
        {
            return await WriteServerFailureAsync(
                stream,
                hello.RequestId,
                hello.Command,
                clock.UtcNow,
                acquisition.Error!,
                cancellationToken).ConfigureAwait(false);
        }

        ReplayLease lease = acquisition.Lease!;
        Guid connectionId = Guid.NewGuid();
        string serverNonce = BrokerHandshakeBinding.CreateNonce();
        BrokerError? replayError = await replayRegistry.MarkChallengeIssuedAsync(
            lease,
            connectionId,
            cancellationToken).ConfigureAwait(false);
        if (replayError is not null)
        {
            return await WriteServerFailureAsync(
                stream,
                hello.RequestId,
                hello.Command,
                clock.UtcNow,
                replayError,
                cancellationToken).ConfigureAwait(false);
        }

        BrokerServerHello serverHello = BrokerServerHello.Succeeded(
            hello.RequestId,
            hello.Command,
            clock.UtcNow,
            connectionId,
            serverNonce);
        BrokerPipeConnectionResult serverHelloWrite = await WriteFrameAsync(
            stream,
            serverHello,
            null,
            cancellationToken).ConfigureAwait(false);
        if (!serverHelloWrite.ResponseWritten)
        {
            await replayRegistry.CompleteAsync(
                lease,
                ReplayState.Abandoned,
                BrokerErrorCodes.FSL_E_HANDSHAKE_EXPIRED,
                CancellationToken.None).ConfigureAwait(false);
            return serverHelloWrite;
        }

        TimeSpan handshakeRemaining = serverHello.Result!.ExpiresUtc - clock.UtcNow;
        if (handshakeRemaining <= TimeSpan.Zero)
        {
            handshakeRemaining = TimeSpan.FromTicks(1);
        }

        BrokerPipeReadResult second = await BrokerPipeFrameCodec.ReadAsync(
            stream,
            handshakeRemaining,
            cancellationToken).ConfigureAwait(false);
        if (!second.IsSuccess)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new BrokerPipeConnectionResult(false, BrokerPipeFrameCodec.CancelledError());
            }

            if (second.TimedOut)
            {
                BrokerError error = Error(
                    BrokerErrorCodes.FSL_E_HANDSHAKE_EXPIRED,
                    "The handshake has expired.",
                    true,
                    null);
                await replayRegistry.CompleteAsync(
                    lease,
                    ReplayState.Abandoned,
                    BrokerErrorCodes.FSL_E_HANDSHAKE_EXPIRED,
                    CancellationToken.None).ConfigureAwait(false);
                return await WriteCommandFailureAsync(
                    stream,
                    hello,
                    connectionId,
                    clock.UtcNow,
                    error,
                    cancellationToken).ConfigureAwait(false);
            }

            await replayRegistry.CompleteAsync(
                lease,
                ReplayState.Failed,
                second.Error!.Code,
                CancellationToken.None).ConfigureAwait(false);
            return await WriteCommandFailureAsync(
                stream,
                hello,
                connectionId,
                clock.UtcNow,
                second.Error,
                cancellationToken).ConfigureAwait(false);
        }

        BrokerHandshakeFrameParseResult secondFrame = BrokerHandshakeProtocolJson.DeserializeFrame(
            second.Body,
            clock.UtcNow,
            durationPolicy);
        if (!secondFrame.IsSuccess)
        {
            await replayRegistry.CompleteAsync(
                lease,
                ReplayState.Failed,
                secondFrame.Error!.Code,
                CancellationToken.None).ConfigureAwait(false);
            return await WriteCommandFailureAsync(
                stream,
                hello,
                connectionId,
                clock.UtcNow,
                secondFrame.Error,
                cancellationToken).ConfigureAwait(false);
        }

        if (secondFrame.Frame is not BrokerCommandRequest commandRequest || second.HasTrailingData)
        {
            BrokerError error = Error(
                BrokerErrorCodes.FSL_E_PROTOCOL_SEQUENCE_INVALID,
                "The protocol message sequence is invalid.",
                false,
                "frameType");
            await replayRegistry.CompleteAsync(
                lease,
                ReplayState.Failed,
                error.Code,
                CancellationToken.None).ConfigureAwait(false);
            return await WriteCommandFailureAsync(
                stream,
                hello,
                connectionId,
                clock.UtcNow,
                error,
                cancellationToken).ConfigureAwait(false);
        }

        BrokerError? bindingError = ValidateCommandRequest(
            commandRequest,
            hello,
            options,
            connectionId,
            serverNonce,
            clock.UtcNow);
        if (bindingError is not null)
        {
            await replayRegistry.CompleteAsync(
                lease,
                ReplayState.Failed,
                bindingError.Code,
                CancellationToken.None).ConfigureAwait(false);
            return await WriteCommandFailureAsync(
                stream,
                hello,
                connectionId,
                clock.UtcNow,
                bindingError,
                cancellationToken).ConfigureAwait(false);
        }

        replayError = await replayRegistry.MarkExecutingAsync(lease, cancellationToken).ConfigureAwait(false);
        if (replayError is not null)
        {
            return await WriteCommandFailureAsync(
                stream,
                hello,
                connectionId,
                clock.UtcNow,
                replayError,
                cancellationToken).ConfigureAwait(false);
        }

        BrokerResponseEnvelope response;
        ReplayState terminalState;
        string? terminalCode;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        execution.CancelAfter(MaximumExecutionDuration);
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
        Task renewalTask = RenewUntilCancelledAsync(replayRegistry, lease, renewal.Token);
        try
        {
            BrokerExecutionOutcome outcome = await processRequest(
                commandRequest.Request,
                execution.Token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(outcome);
            response = outcome.Response;
            (terminalState, terminalCode) = outcome.Effect switch
            {
                BrokerExecutionEffect.Succeeded => (ReplayState.Succeeded, null),
                BrokerExecutionEffect.FailedWithoutSideEffects => (ReplayState.Failed, response.Error!.Code),
                BrokerExecutionEffect.RolledBack => (ReplayState.RolledBack, response.Error!.Code),
                BrokerExecutionEffect.RecoveryRequired => (
                    ReplayState.RecoveryRequired,
                    BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED),
                _ => throw new InvalidOperationException("The broker execution effect is invalid."),
            };
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            terminalState = ReplayState.RecoveryRequired;
            terminalCode = BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED;
            response = BrokerResponseEnvelope.Failed(
                hello.RequestId,
                hello.Command,
                clock.UtcNow,
                Error(
                    BrokerErrorCodes.FSL_E_OPERATION_CANCELLED,
                    "The operation was cancelled.",
                    false,
                    null));
        }
        catch (Exception)
        {
            terminalState = ReplayState.RecoveryRequired;
            terminalCode = BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED;
            response = BrokerResponseEnvelope.Failed(
                hello.RequestId,
                hello.Command,
                clock.UtcNow,
                BrokerError.Internal());
        }
        finally
        {
            renewal.Cancel();
            try
            {
                await renewalTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        replayError = await replayRegistry.CompleteAsync(
            lease,
            terminalState,
            terminalCode,
            CancellationToken.None).ConfigureAwait(false);
        if (replayError is not null)
        {
            response = BrokerResponseEnvelope.Failed(
                hello.RequestId,
                hello.Command,
                clock.UtcNow,
                replayError);
        }

        return await WriteFrameAsync(
            stream,
            new BrokerCommandResponse(
                BrokerFrameType.CommandResponse,
                BrokerProtocolConstants.HandshakeVersion,
                BrokerProtocolConstants.ProtocolVersion,
                hello.RequestId,
                hello.Command,
                connectionId,
                response),
            response.Error,
            cancellationToken).ConfigureAwait(false);
    }

    private static BrokerError? ValidateClientHello(
        BrokerClientHello hello,
        BrokerConsentOptions options,
        DateTimeOffset serverTimeUtc)
    {
        if (hello.RequestId != options.RequestId)
        {
            return BindingMismatch("requestId");
        }

        if (hello.ClientSessionId != options.SessionId)
        {
            return Error(
                BrokerErrorCodes.FSL_E_SESSION_MISMATCH,
                "The broker and client do not belong to the same Windows session.",
                false,
                "clientSessionId");
        }

        return (hello.SentAtUtc - serverTimeUtc.ToUniversalTime()).Duration() > RequestTimeWindow
            ? Error(
                BrokerErrorCodes.FSL_E_REQUEST_EXPIRED,
                "The request timestamp is outside the allowed time window.",
                false,
                "sentAtUtc")
            : null;
    }

    private static BrokerError? ValidateCommandRequest(
        BrokerCommandRequest commandRequest,
        BrokerClientHello hello,
        BrokerConsentOptions options,
        Guid connectionId,
        string serverNonce,
        DateTimeOffset serverTimeUtc)
    {
        if (commandRequest.HandshakeVersion != BrokerProtocolConstants.HandshakeVersion
            || commandRequest.ProtocolVersion != hello.ProtocolVersion
            || commandRequest.RequestId != hello.RequestId
            || commandRequest.RequestId != options.RequestId
            || commandRequest.Command != hello.Command
            || commandRequest.ConnectionId != connectionId
            || commandRequest.Request.ProtocolVersion != hello.ProtocolVersion
            || commandRequest.Request.RequestId != hello.RequestId
            || commandRequest.Request.Command != hello.Command)
        {
            return BindingMismatch("bindingProof");
        }

        if (commandRequest.Request.ClientSessionId != hello.ClientSessionId
            || commandRequest.Request.ClientSessionId != options.SessionId)
        {
            return Error(
                BrokerErrorCodes.FSL_E_SESSION_MISMATCH,
                "The broker and client do not belong to the same Windows session.",
                false,
                "clientSessionId");
        }

        if ((commandRequest.Request.SentAtUtc - serverTimeUtc.ToUniversalTime()).Duration() > RequestTimeWindow)
        {
            return Error(
                BrokerErrorCodes.FSL_E_REQUEST_EXPIRED,
                "The request timestamp is outside the allowed time window.",
                false,
                "sentAtUtc");
        }

        return BrokerHandshakeBinding.VerifyProof(
            commandRequest.BindingProof,
            hello.RequestId,
            hello.Command,
            connectionId,
            hello.ClientNonce,
            serverNonce,
            hello.ClientSessionId)
                ? null
                : BindingMismatch("bindingProof");
    }

    private static async Task RenewUntilCancelledAsync(
        IReplayRegistry replayRegistry,
        ReplayLease lease,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(FileReplayRegistry.RenewalPeriod, cancellationToken).ConfigureAwait(false);
            BrokerError? error = await replayRegistry.RenewAsync(lease, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                throw new InvalidOperationException("The Replay lease could not be renewed.");
            }
        }
    }

    private static async ValueTask<BrokerPipeConnectionResult> WriteServerFailureAsync(
        Stream stream,
        Guid? requestId,
        BrokerCommand? command,
        DateTimeOffset serverTimeUtc,
        BrokerError error,
        CancellationToken cancellationToken) => await WriteFrameAsync(
            stream,
            BrokerServerHello.Failed(requestId, command, serverTimeUtc, error),
            error,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<BrokerPipeConnectionResult> WriteCommandFailureAsync(
        Stream stream,
        BrokerClientHello hello,
        Guid connectionId,
        DateTimeOffset serverTimeUtc,
        BrokerError error,
        CancellationToken cancellationToken) => await WriteFrameAsync(
            stream,
            new BrokerCommandResponse(
                BrokerFrameType.CommandResponse,
                BrokerProtocolConstants.HandshakeVersion,
                BrokerProtocolConstants.ProtocolVersion,
                hello.RequestId,
                hello.Command,
                connectionId,
                BrokerResponseEnvelope.Failed(
                    hello.RequestId,
                    hello.Command,
                    serverTimeUtc,
                    error)),
            error,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<BrokerPipeConnectionResult> WriteFrameAsync(
        Stream stream,
        object frame,
        BrokerError? error,
        CancellationToken cancellationToken)
    {
        try
        {
            await BrokerPipeFrameCodec.WriteAsync(
                stream,
                BrokerHandshakeProtocolJson.SerializeFrame(frame),
                cancellationToken).ConfigureAwait(false);
            return new BrokerPipeConnectionResult(true, error);
        }
        catch (OperationCanceledException)
        {
            return new BrokerPipeConnectionResult(false, BrokerPipeFrameCodec.CancelledError());
        }
        catch (UnauthorizedAccessException)
        {
            return new BrokerPipeConnectionResult(false, BrokerPipeFrameCodec.PipeAccessDeniedError());
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return new BrokerPipeConnectionResult(false, BrokerPipeFrameCodec.MalformedError());
        }
    }

    private static Guid? FrameRequestId(object frame) => frame switch
    {
        BrokerClientHello value => value.RequestId,
        BrokerServerHello value => value.RequestId,
        BrokerCommandRequest value => value.RequestId,
        BrokerCommandResponse value => value.RequestId,
        _ => null,
    };

    private static BrokerCommand? FrameCommand(object frame) => frame switch
    {
        BrokerClientHello value => value.Command,
        BrokerServerHello value when Enum.TryParse(value.Command, false, out BrokerCommand command) => command,
        BrokerCommandRequest value => value.Command,
        BrokerCommandResponse value => value.Command,
        _ => null,
    };

    private static BrokerError BindingMismatch(string field) => Error(
        BrokerErrorCodes.FSL_E_REQUEST_BINDING_MISMATCH,
        "The request is not bound to the active handshake.",
        false,
        field);

    private static BrokerError Error(string code, string message, bool retryable, string? field) =>
        new(code, message, retryable, field);
}
