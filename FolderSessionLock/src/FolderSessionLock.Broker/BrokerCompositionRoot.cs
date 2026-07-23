using FolderSessionLock.Broker.Lifecycle;
using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker;

public sealed class BrokerCompositionRoot
{
    private readonly RecoveryTaskRegistry _recoveryRegistry = new();

    public BrokerConsentSecurityRuntime CreateConsentSecurityRuntime(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ProtectedPathSet pathSet = ProtectedPathSet.CreateProduction();
        return new BrokerConsentSecurityRuntime(
            new WindowsBrokerConnectionAuthenticator(),
            FileReplayRegistry.CreateProduction(
                pathSet,
                clock,
                new RecoveryReplaySideEffectEvidenceProvider(_recoveryRegistry)));
    }

    public BrokerRuntime CreateRuntime(
        string repositoryRoot,
        string installationRoot,
        IEnumerable<string> synchronizationRoots,
        LockDurationPolicy durationPolicy,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(durationPolicy);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var clock = new SystemClock();
        var pathValidator = new WindowsFolderPathValidator(new FolderPathSafetyPolicy(
            repositoryRoot,
            installationRoot,
            synchronizationRoots));
        return CreateRuntimeCore(
            pathValidator,
            durationPolicy,
            loggerFactory,
            clock,
            new UnavailableRecoveryReadinessReader());
    }

    internal BrokerRuntime CreateProductionConsentRuntime(
        ConsentBrokerBootstrapIdentity identity,
        ILoggerFactory loggerFactory,
        IClock clock,
        LockDurationPolicy durationPolicy)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(durationPolicy);
        ProtectedPathSet protectedPaths = ProtectedPathSet.CreateProduction();
        var tokenSource = new RetainedInitiatingUserTokenSource(identity.InitiatingToken);
        var pathValidator = new WindowsFolderPathValidator(
            new FolderPathSafetyPolicy(protectedPaths.InstallDirectory),
            new WindowsRepositoryPathClassifier(),
            new WindowsSynchronizationPathClassifier(tokenSource));
        return CreateRuntimeCore(
            pathValidator,
            durationPolicy,
            loggerFactory,
            clock,
            WindowsRecoveryReadinessStore.CreateProduction(clock));
    }

    private BrokerRuntime CreateRuntimeCore(
        WindowsFolderPathValidator pathValidator,
        LockDurationPolicy durationPolicy,
        ILoggerFactory loggerFactory,
        IClock clock,
        IRecoveryReadinessReader readinessReader)
    {
        var pathRelationService = new WindowsFolderPathRelationService();
        ProtectedPathSet protectedPaths = ProtectedPathSet.CreateProduction();
        var recoveryWriteSafety = new RecoveryStoreWriteSafetyState();
        var recoveryStore = FileRecoveryRecordStore.CreateProduction(
            protectedPaths,
            recoveryWriteSafety);
        var recoveryTransaction = new RecoveryRecordTransaction(
            recoveryStore,
            _recoveryRegistry,
            clock);
        var aclEditor = new DirectoryAclEditor();
        var folderLockService = new WindowsFolderLockService(
            new WindowsSessionIdentityProvider(),
            pathValidator,
            pathRelationService,
            aclEditor,
            recoveryTransaction);
        var recoveryAclCleanup = new RecoveryRecordAclCleanup(
            recoveryStore,
            pathValidator,
            aclEditor,
            clock);
        var taskManager = new LockTaskManager(pathRelationService);
        var coordinator = new LockTaskCoordinator(
            taskManager,
            folderLockService,
            clock,
            loggerFactory.CreateLogger<LockTaskCoordinator>());
        var scheduler = new LockTaskScheduler(
            coordinator,
            clock,
            loggerFactory.CreateLogger<LockTaskScheduler>());
        var lifecycleController = new BrokerLifecycleController(
            scheduler,
            coordinator,
            loggerFactory.CreateLogger<BrokerLifecycleController>());
        var commandProcessor = new BrokerCommandProcessor(
            pathValidator,
            taskManager,
            coordinator,
            folderLockService,
            _recoveryRegistry,
            clock,
            durationPolicy,
            new RecoveryCreateLockGate(
                readinessReader,
                recoveryWriteSafety));

        return new BrokerRuntime(
            folderLockService,
            lifecycleController,
            commandProcessor,
            recoveryAclCleanup,
            taskManager);
    }

    internal RecoveryRuntime CreateRecoveryRuntime(
        IRecoveryReadinessStore readinessStore,
        IRecoveryServiceStatusReporter statusReporter,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(readinessStore);
        ArgumentNullException.ThrowIfNull(statusReporter);
        ProtectedPathSet protectedPaths = ProtectedPathSet.CreateProduction();
        var clock = new SystemClock();
        var recoveryWriteSafety = new RecoveryStoreWriteSafetyState();
        var store = FileRecoveryRecordStore.CreateProduction(
            protectedPaths,
            recoveryWriteSafety);
        var pathValidator = new WindowsFolderPathValidator(new FolderPathSafetyPolicy(
            protectedPaths.InstallDirectory,
            protectedPaths.InstallDirectory,
            []));
        var cleanup = new RecoveryRecordAclCleanup(
            store,
            pathValidator,
            new DirectoryAclEditor(),
            clock);
        var batch = new RecoveryBatchRunner(
            new WindowsProtectedPathSecurityVerifier(protectedPaths),
            protectedPaths.CreateRequests(),
            new RecoveryDirectoryEnumerator(
                store.RecordsDirectory,
                new WindowsRecoveryRecordFileSecurity(),
                new WindowsRecoveryStoreFilePlatform()),
            cleanup);
        return new RecoveryRuntime(
            new RecoveryOnceRunner(batch),
            new RecoveryServiceOrchestrator(
                batch,
                readinessStore,
                statusReporter,
                loggerFactory: loggerFactory));
    }
}

public sealed class BrokerRuntime
{
    internal BrokerRuntime(
        IFolderLockService folderLockService,
        BrokerLifecycleController lifecycleController,
        BrokerCommandProcessor commandProcessor,
        RecoveryRecordAclCleanup recoveryAclCleanup,
        LockTaskManager taskManager)
    {
        FolderLockService = folderLockService;
        LifecycleController = lifecycleController;
        CommandProcessor = commandProcessor;
        RecoveryAclCleanup = recoveryAclCleanup;
        TaskManager = taskManager;
    }

    public IFolderLockService FolderLockService { get; }

    internal BrokerLifecycleController LifecycleController { get; }

    internal BrokerCommandProcessor CommandProcessor { get; }

    internal RecoveryRecordAclCleanup RecoveryAclCleanup { get; }

    internal LockTaskManager TaskManager { get; }

    internal bool HasRecoveryRequired =>
        TaskManager.GetAll().Any(task => task.Status == LockTaskStatus.RecoveryRequired);
}

public sealed record BrokerConsentSecurityRuntime(
    IBrokerConnectionAuthenticator Authenticator,
    IReplayRegistry ReplayRegistry);

internal sealed record RecoveryRuntime(
    RecoveryOnceRunner OnceRunner,
    RecoveryServiceOrchestrator ServiceOrchestrator);

internal sealed class RetainedInitiatingUserTokenSource(SafeAccessTokenHandle token)
    : IInitiatingUserTokenSource
{
    public Result<SafeAccessTokenHandle> GetToken() =>
        token.IsClosed || token.IsInvalid
            ? Result<SafeAccessTokenHandle>.Failure(new Error(
                BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
                BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
                ErrorCategory.UnrecoverableError))
            : Result<SafeAccessTokenHandle>.Success(token);
}
