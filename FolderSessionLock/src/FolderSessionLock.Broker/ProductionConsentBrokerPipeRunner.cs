using System.IO.Pipes;
using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Broker.Transport;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Services;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.Broker;

internal sealed record ConsentBrokerProductionSession(
    LockDurationPolicy DurationPolicy,
    IClock Clock,
    IBrokerConnectionAuthenticator Authenticator,
    IReplayRegistry ReplayRegistry,
    IConsentBrokerSessionRuntime Runtime);

internal interface IConsentBrokerProductionSessionFactory
{
    ConsentBrokerProductionSession Create(
        ConsentBrokerBootstrapIdentity identity,
        ILoggerFactory loggerFactory,
        IClock clock);
}

internal interface IConsentBrokerSessionRuntime
{
    ValueTask<BrokerExecutionOutcome> ProcessAsync(
        BrokerRequestEnvelope request,
        BrokerExecutionContext executionContext,
        CancellationToken cancellationToken);

    Task<Result> RunSchedulerAsync();

    ValueTask<Result<int>> StopAsync();

    bool HasRecoveryRequired { get; }
}

internal interface IConsentBrokerPipeServer
{
    IConsentBrokerPipeListener? Create(ConsentBrokerBootstrapIdentity identity);
}

internal interface IConsentBrokerPipeListener : IAsyncDisposable
{
    ValueTask<BrokerPipeConnectionResult> RunOnceAsync(
        BrokerConsentOptions options,
        ConsentBrokerProductionSession session,
        Func<BrokerRequestEnvelope, CancellationToken, ValueTask<BrokerExecutionOutcome>> processRequest,
        CancellationToken cancellationToken);
}

internal sealed class ProductionConsentBrokerPipeRunner : IConsentBrokerPipeRunner
{
    private readonly IConsentBrokerProductionSessionFactory _sessionFactory;
    private readonly IConsentBrokerPipeServer _pipeServer;
    private readonly IClock _clock;

    internal ProductionConsentBrokerPipeRunner()
        : this(
            new ConsentBrokerProductionSessionFactory(),
            new WindowsConsentBrokerPipeServer(),
            new SystemClock())
    {
    }

    internal ProductionConsentBrokerPipeRunner(
        IConsentBrokerProductionSessionFactory sessionFactory,
        IConsentBrokerPipeServer pipeServer,
        IClock clock)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _pipeServer = pipeServer ?? throw new ArgumentNullException(nameof(pipeServer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<ConsentBrokerPipeRunResult> RunAsync(
        BrokerConsentOptions options,
        ConsentBrokerBootstrapIdentity identity,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        IProtectedLoggerHealth? loggerHealth = loggerFactory as IProtectedLoggerHealth;

        IConsentBrokerPipeListener? listener = _pipeServer.Create(identity);
        if (listener is null)
        {
            return new ConsentBrokerPipeRunResult(PipeInitializationFailed: true);
        }

        ConsentBrokerProductionSession session;
        BrokerRequestEnvelope? processedRequest = null;
        BrokerExecutionOutcome? processedOutcome = null;
        BrokerPipeConnectionResult connection;
        await using (listener.ConfigureAwait(false))
        {
            session = _sessionFactory.Create(
                identity,
                loggerFactory,
                _clock);
            connection = await listener.RunOnceAsync(
                options,
                session,
                async (request, requestCancellationToken) =>
                {
                    BrokerExecutionOutcome outcome = await session.Runtime.ProcessAsync(
                        request,
                        BrokerExecutionContext.OrdinaryUi,
                        requestCancellationToken).ConfigureAwait(false);
                    processedRequest = request;
                    processedOutcome = outcome;
                    return outcome;
                },
                cancellationToken).ConfigureAwait(false);
        }

        bool activeCreateLock = processedRequest?.Command == BrokerCommand.CreateLock
            && processedOutcome is
            {
                Effect: BrokerExecutionEffect.Succeeded,
                Response.Success: true,
            };
        if (activeCreateLock)
        {
            try
            {
                await session.Runtime.RunSchedulerAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // StopAsync owns the stable scheduler-diagnostic and Cleanup policy.
            }
        }

        Result<int> cleanup;
        try
        {
            cleanup = await session.Runtime.StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new ConsentBrokerPipeRunResult(LifecycleCleanupFailed: true);
        }

        if (cleanup.IsFailure || session.Runtime.HasRecoveryRequired)
        {
            return new ConsentBrokerPipeRunResult(LifecycleCleanupFailed: true);
        }

        bool protectedLoggerFailed = loggerHealth is { IsPermanentlyFailed: true };

        if (connection.Error?.Code == BrokerErrorCodes.FSL_E_PIPE_INITIALIZATION_FAILED)
        {
            return new ConsentBrokerPipeRunResult(PipeInitializationFailed: true);
        }

        if (connection.Error?.Code == BrokerErrorCodes.FSL_E_BROKER_CONNECT_TIMEOUT)
        {
            return new ConsentBrokerPipeRunResult(ClientConnectTimeout: true);
        }

        if (!connection.ResponseWritten)
        {
            return processedOutcome is null
                ? new ConsentBrokerPipeRunResult(
                    ProtocolFailedBeforeResponse: true,
                    ProtectedLoggerFailed: protectedLoggerFailed)
                : new ConsentBrokerPipeRunResult(
                    ResponseWriteFailed: true,
                    ProtectedLoggerFailed: protectedLoggerFailed);
        }

        return new ConsentBrokerPipeRunResult(
            ResponseWritten: true,
            ProtectedLoggerFailed: protectedLoggerFailed);
    }
}

internal sealed class ConsentBrokerProductionSessionFactory
    : IConsentBrokerProductionSessionFactory
{
    private readonly BrokerCompositionRoot _compositionRoot = new();

    public ConsentBrokerProductionSession Create(
        ConsentBrokerBootstrapIdentity identity,
        ILoggerFactory loggerFactory,
        IClock clock)
    {
        LockDurationPolicy durationPolicy = LockDurationPolicy.CreateProduction();
        BrokerRuntime runtime = _compositionRoot.CreateProductionConsentRuntime(
            identity,
            loggerFactory,
            clock,
            durationPolicy);
        BrokerConsentSecurityRuntime security = _compositionRoot.CreateConsentSecurityRuntime(clock);
        return new ConsentBrokerProductionSession(
            durationPolicy,
            clock,
            security.Authenticator,
            security.ReplayRegistry,
            new BrokerConsentSessionRuntime(runtime));
    }
}

internal sealed class BrokerConsentSessionRuntime : IConsentBrokerSessionRuntime
{
    internal BrokerConsentSessionRuntime(BrokerRuntime runtime)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    internal BrokerRuntime Runtime { get; }

    public ValueTask<BrokerExecutionOutcome> ProcessAsync(
        BrokerRequestEnvelope request,
        BrokerExecutionContext executionContext,
        CancellationToken cancellationToken) => Runtime.CommandProcessor.ProcessAsync(
            request,
            executionContext,
            cancellationToken);

    public Task<Result> RunSchedulerAsync() => Runtime.LifecycleController.RunSchedulerAsync();

    public ValueTask<Result<int>> StopAsync() => Runtime.LifecycleController.StopAsync();

    public bool HasRecoveryRequired => Runtime.HasRecoveryRequired;
}

internal sealed class WindowsConsentBrokerPipeServer : IConsentBrokerPipeServer
{
    public IConsentBrokerPipeListener? Create(ConsentBrokerBootstrapIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            return new WindowsConsentBrokerPipeListener(BrokerPipeServer.Create(
                identity.InitiatingLogonSid,
                identity.BrokerAccountSid));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

internal sealed class WindowsConsentBrokerPipeListener(NamedPipeServerStream pipe)
    : IConsentBrokerPipeListener
{
    public ValueTask<BrokerPipeConnectionResult> RunOnceAsync(
        BrokerConsentOptions options,
        ConsentBrokerProductionSession session,
        Func<BrokerRequestEnvelope, CancellationToken, ValueTask<BrokerExecutionOutcome>> processRequest,
        CancellationToken cancellationToken) => BrokerPipeServer.RunCreatedOnceAsync(
            pipe,
            options,
            session.DurationPolicy,
            session.Clock,
            session.Authenticator,
            session.ReplayRegistry,
            processRequest,
            cancellationToken);

    public ValueTask DisposeAsync() => pipe.DisposeAsync();
}
