using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.Broker;

internal sealed record ConsentBrokerPipeRunResult(
    bool ResponseWritten = false,
    bool PipeInitializationFailed = false,
    bool ClientConnectTimeout = false,
    bool ProtocolFailedBeforeResponse = false,
    bool ResponseWriteFailed = false,
    bool LifecycleCleanupFailed = false,
    bool ProtectedLoggerFailed = false,
    bool InternalFailure = false);

internal interface IConsentBrokerPipeRunner
{
    ValueTask<ConsentBrokerPipeRunResult> RunAsync(
        BrokerConsentOptions options,
        ConsentBrokerBootstrapIdentity identity,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken);
}

internal sealed class ConsentBrokerHost
{
    private readonly IProtectedLoggerFactory _loggerFactory;
    private readonly IConsentBrokerBootstrapIdentityVerifier _identityVerifier;
    private readonly IConsentBrokerPipeRunner _pipeRunner;
    private readonly Func<Guid> _instanceIdFactory;

    internal ConsentBrokerHost(
        IProtectedLoggerFactory loggerFactory,
        IConsentBrokerBootstrapIdentityVerifier identityVerifier,
        IConsentBrokerPipeRunner pipeRunner,
        Func<Guid>? instanceIdFactory = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _identityVerifier = identityVerifier
            ?? throw new ArgumentNullException(nameof(identityVerifier));
        _pipeRunner = pipeRunner ?? throw new ArgumentNullException(nameof(pipeRunner));
        _instanceIdFactory = instanceIdFactory ?? Guid.NewGuid;
    }

    internal async ValueTask<ConsentBrokerExitCode> RunAsync(
        BrokerConsentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Guid instanceId = _instanceIdFactory();
        if (instanceId == Guid.Empty)
        {
            return ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure;
        }

        Result<ILoggerFactory> logger = _loggerFactory.Create(
            ProtectedLoggerMode.ConsentBroker,
            instanceId);
        if (logger.IsFailure)
        {
            return ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure;
        }

        using ILoggerFactory protectedLogger = logger.Value;
        IProtectedLoggerHealth? loggerHealth = protectedLogger as IProtectedLoggerHealth;
        try
        {
            ConsentBrokerBootstrapIdentityResult identity = await _identityVerifier
                .VerifyAsync(options, cancellationToken)
                .ConfigureAwait(false);
            if (!identity.IsSuccess)
            {
                return loggerHealth is { IsPermanentlyFailed: true }
                    ? ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure
                    : identity.ExitCode;
            }

            using ConsentBrokerBootstrapIdentity verifiedIdentity = identity.Identity!;
            ConsentBrokerPipeRunResult result = await _pipeRunner
                .RunAsync(
                    options,
                    verifiedIdentity,
                    protectedLogger,
                    cancellationToken)
                .ConfigureAwait(false);
            if (loggerHealth is { IsPermanentlyFailed: true })
            {
                result = result with { ProtectedLoggerFailed = true };
            }

            return ConsentBrokerExitPolicy.Map(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ConsentBrokerExitCode.ProtocolFailedBeforeResponse;
        }
        catch (Exception)
        {
            return ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure;
        }
    }
}

internal static class ConsentBrokerExitPolicy
{
    internal static ConsentBrokerExitCode Map(ConsentBrokerPipeRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.LifecycleCleanupFailed)
        {
            return ConsentBrokerExitCode.LifecycleCleanupFailed;
        }

        if (result.ResponseWritten)
        {
            return ConsentBrokerExitCode.ProtocolHandledOrLifecycleCompleted;
        }

        if (result.ProtectedLoggerFailed)
        {
            return ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure;
        }

        if (result.ResponseWriteFailed)
        {
            return ConsentBrokerExitCode.ResponseWriteFailed;
        }

        if (result.ProtocolFailedBeforeResponse)
        {
            return ConsentBrokerExitCode.ProtocolFailedBeforeResponse;
        }

        if (result.PipeInitializationFailed)
        {
            return ConsentBrokerExitCode.PipeInitializationFailed;
        }

        if (result.ClientConnectTimeout)
        {
            return ConsentBrokerExitCode.ClientConnectTimeout;
        }

        if (result.InternalFailure)
        {
            return ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure;
        }

        return ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure;
    }
}
