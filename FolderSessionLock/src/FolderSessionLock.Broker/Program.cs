using FolderSessionLock.Broker;
using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Services;
using Microsoft.Extensions.Logging;

if (!BrokerStartupOptions.TryParse(args, out BrokerStartupOptions? options))
{
    return 2;
}

if (options!.RunMode == BrokerRunMode.ConsentBroker)
{
    var host = new ConsentBrokerHost(
        new WindowsProtectedLoggerFactory(),
        new ConsentBrokerBootstrapIdentityVerifier(),
        new ProductionConsentBrokerPipeRunner());
    ConsentBrokerExitCode consentExitCode = await host.RunAsync(options.ConsentOptions!);
    return (int)consentExitCode;
}

if (options.RunMode == BrokerRunMode.RecoveryService)
{
    return new WindowsRecoveryServiceHost(
        new WindowsRecoveryServiceDispatcher(),
        RunRecoveryServiceAsync).Run();
}

Result<ILoggerFactory> recoveryLoggerResult = new WindowsProtectedLoggerFactory().Create(
    ProtectedLoggerMode.RecoveryOnce,
    Guid.NewGuid());
if (recoveryLoggerResult.IsFailure)
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(LoggerUnavailableSummary()));
    return (int)RecoveryOnceExitCode.InternalFailure;
}

using ILoggerFactory recoveryLogger = recoveryLoggerResult.Value;
var recoveryClock = new SystemClock();
RecoveryRuntime recovery = new BrokerCompositionRoot().CreateRecoveryRuntime(
    WindowsRecoveryReadinessStore.CreateProduction(recoveryClock),
    new RecoveryOnceStatusReporter(),
    recoveryLogger);
RecoveryOnceExitCode exitCode = await recovery.OnceRunner.RunAsync(argumentsValid: true);
LogRecoveryState(recoveryLogger, recovery.OnceRunner.LastSummary?.primaryErrorCode);
if (recoveryLogger is IProtectedLoggerHealth { IsPermanentlyFailed: true })
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(LoggerUnavailableSummary()));
    return (int)RecoveryOnceExitCode.InternalFailure;
}

if (recovery.OnceRunner.LastSummary is { } summary)
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary));
}

return (int)exitCode;

static async Task<int> RunRecoveryServiceAsync(
    IRecoveryServiceStatusReporter statusReporter,
    CancellationToken stopToken)
{
    Result<ILoggerFactory> loggerResult = new WindowsProtectedLoggerFactory().Create(
        ProtectedLoggerMode.RecoveryService,
        Guid.NewGuid());
    if (loggerResult.IsFailure)
    {
        await statusReporter.ReportAsync(
            new RecoveryServiceStatusSnapshot(
                RecoveryServiceState.Stopped,
                false,
                0,
                TimeSpan.Zero,
                BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE),
            CancellationToken.None);
        return (int)RecoveryOnceExitCode.InternalFailure;
    }

    using ILoggerFactory loggerFactory = loggerResult.Value;
    var clock = new SystemClock();
    RecoveryRuntime runtime = new BrokerCompositionRoot().CreateRecoveryRuntime(
        WindowsRecoveryReadinessStore.CreateProduction(clock),
        statusReporter,
        loggerFactory);
    Task<RecoveryServiceState> start = runtime.ServiceOrchestrator.StartAsync();
    Task externalStop = WaitForCancellationAsync(stopToken);
    Task internalStop = runtime.ServiceOrchestrator.WaitForStopRequestAsync();
    Task first = await Task.WhenAny(start, externalStop, internalStop);
    if (first == start)
    {
        RecoveryServiceState state = await start;
        if (state == RecoveryServiceState.Stopped)
        {
            return (int)RecoveryOnceExitCode.InternalFailure;
        }

        await Task.WhenAny(externalStop, internalStop);
    }

    try
    {
        await runtime.ServiceOrchestrator.StopAsync(CancellationToken.None);
        return 0;
    }
    finally
    {
        await runtime.ServiceOrchestrator.WaitForCompletionAsync();
    }
}

static Task WaitForCancellationAsync(CancellationToken cancellationToken)
{
    if (cancellationToken.IsCancellationRequested)
    {
        return Task.CompletedTask;
    }

    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    cancellationToken.Register(
        static state => ((TaskCompletionSource)state!).TrySetResult(),
        completion);
    return completion.Task;
}

static RecoveryRunSummary LoggerUnavailableSummary() => new(
    0,
    0,
    0,
    0,
    0,
    0,
    0,
    0,
    0,
    0,
    true,
    BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE);

static void LogRecoveryState(ILoggerFactory loggerFactory, string? errorCode)
{
    ProtectedLogEvent entry = ProtectedLogEventCatalog.ReadinessStateChanged;
    loggerFactory.CreateLogger("FolderSessionLock.Broker.RecoveryOnce").Log(
        LogLevel.Information,
        new EventId(entry.EventId, entry.EventName),
        new ProtectedLogContext(ErrorCode: errorCode),
        null,
        static (_, _) => ProtectedLogEventCatalog.ReadinessStateChanged.Message);
}
