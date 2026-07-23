using System.Reflection;
using System.Security.Principal;
using System.Xml.Linq;
using FolderSessionLock.Broker;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.App.Tests;

public sealed class ProcessBoundaryTests
{
    [Fact]
    public void AppAssembly_DoesNotReferenceBrokerWindowsOrAclAssemblies()
    {
        string[] references = typeof(App).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();

        Assert.DoesNotContain("FolderSessionLock.Broker", references);
        Assert.DoesNotContain("FolderSessionLock.Windows", references);
        Assert.DoesNotContain("System.IO.FileSystem.AccessControl", references);
    }

    [Fact]
    public void BrokerAssembly_ReferencesCoreAndWindows()
    {
        string[] references = typeof(BrokerCompositionRoot).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();

        Assert.Contains("FolderSessionLock.Core", references);
        Assert.Contains("FolderSessionLock.Windows", references);
    }

    [Fact]
    public void BrokerCompositionRoot_OwnsFolderLockServiceAndLifecycleController()
    {
        var compositionRoot = new BrokerCompositionRoot();

        BrokerRuntime runtime = compositionRoot.CreateRuntime(
            Path.Combine(Path.GetTempPath(), "FolderSessionLock.RepositoryBoundary"),
            Path.Combine(Path.GetTempPath(), "FolderSessionLock.InstallationBoundary"),
            [],
            LockDurationPolicy.Create(TimeSpan.FromMinutes(1), TimeSpan.FromHours(24)).Value,
            new RecordingLoggerFactory());

        Assert.IsType<WindowsFolderLockService>(runtime.FolderLockService);
        Assert.IsType<BrokerLifecycleController>(runtime.LifecycleController);
        Assert.IsType<RecoveryRecordAclCleanup>(runtime.RecoveryAclCleanup);
        RecoveryCreateLockGate gate = GetPrivateField<RecoveryCreateLockGate>(
            runtime.CommandProcessor,
            "_createLockGate");
        Assert.IsType<UnavailableRecoveryReadinessReader>(
            GetPrivateField<IRecoveryReadinessReader>(gate, "_reader"));
        Assert.DoesNotContain(
            typeof(BrokerRuntime).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => typeof(ILockTaskScheduler).IsAssignableFrom(property.PropertyType));
        Assert.DoesNotContain(
            typeof(BrokerRuntime).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.PropertyType == typeof(RecoveryRecordAclCleanup));
    }

    [Fact]
    public async Task BrokerCompositionRoot_UsesCallerLoggersForSanitizedCleanupDiagnostics()
    {
        string sensitiveRepositoryRoot = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.RepositoryBoundary.SensitiveToken");
        string sensitiveInstallationRoot = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.InstallationBoundary.SensitiveToken");
        var loggerFactory = new RecordingLoggerFactory();
        var compositionRoot = new BrokerCompositionRoot();
        BrokerRuntime runtime = compositionRoot.CreateRuntime(
            sensitiveRepositoryRoot,
            sensitiveInstallationRoot,
            [],
            LockDurationPolicy.Create(TimeSpan.FromMinutes(1), TimeSpan.FromHours(24)).Value,
            loggerFactory);

        var lifecycleLogger = GetPrivateField<ILogger<BrokerLifecycleController>>(
            runtime.LifecycleController,
            "_logger");
        var coordinator = GetPrivateField<LockTaskCoordinator>(
            runtime.LifecycleController,
            "_coordinator");
        var scheduler = GetPrivateField<LockTaskScheduler>(
            runtime.LifecycleController,
            "_scheduler");
        Assert.NotSame(NullLogger<BrokerLifecycleController>.Instance, lifecycleLogger);
        Assert.NotSame(
            NullLogger<LockTaskCoordinator>.Instance,
            GetPrivateField<ILogger<LockTaskCoordinator>>(coordinator, "_logger"));
        Assert.NotSame(
            NullLogger<LockTaskScheduler>.Instance,
            GetPrivateField<ILogger<LockTaskScheduler>>(scheduler, "_logger"));

        Result<int> result = await runtime.LifecycleController.StopAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value);
        Assert.Contains(
            loggerFactory.Entries,
            entry => entry.Category == typeof(LockTaskCoordinator).FullName
                && entry.Message.Contains(
                    "Administrative cleanup completed. FullyTraversed: True. SuccessCount: 0. ErrorCount: 0. RecoveryRequired: False.",
                    StringComparison.Ordinal));
        Assert.Contains(
            loggerFactory.Entries,
            entry => entry.Category == typeof(BrokerLifecycleController).FullName
                && entry.Message.Contains(
                    "The broker lifecycle cleanup completed.",
                    StringComparison.Ordinal));
        Assert.All(loggerFactory.Entries, entry => Assert.Null(entry.Exception));
        string log = string.Join(
            Environment.NewLine,
            loggerFactory.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain(sensitiveRepositoryRoot, log, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveInstallationRoot, log, StringComparison.Ordinal);
        Assert.DoesNotContain("SensitiveToken", log, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductExecutables_OnlyBrokerReferencesWindows()
    {
        string solutionRoot = FindSolutionRoot();
        string sourceRoot = Path.Combine(solutionRoot, "src");
        string[] executableProjects = Directory
            .EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(IsExecutableProject)
            .ToArray();

        string[] windowsConsumers = executableProjects
            .Where(project => GetProjectReferences(project).Contains("FolderSessionLock.Windows"))
            .Select(Path.GetFileNameWithoutExtension)
            .ToArray()!;

        Assert.Equal(["FolderSessionLock.Broker"], windowsConsumers);
        Assert.Equal(
            ["FolderSessionLock.Core", "FolderSessionLock.Windows"],
            GetProjectReferences(Path.Combine(
                sourceRoot,
                "FolderSessionLock.Broker",
                "FolderSessionLock.Broker.csproj")));
        Assert.Equal(
            ["FolderSessionLock.Core"],
            GetProjectReferences(Path.Combine(
                sourceRoot,
                "FolderSessionLock.App",
                "FolderSessionLock.App.csproj")));
    }

    [Fact]
    public void RecoveryComposition_IsInternalFixedPathAndProductionVerifierOnly()
    {
        var compositionRoot = new BrokerCompositionRoot();
        RecoveryRuntime runtime = compositionRoot.CreateRecoveryRuntime(
            new UnavailableRecoveryReadinessPublisher(),
            new RecoveryOnceStatusReporter());
        RecoveryBatchRunner batch = GetPrivateField<RecoveryBatchRunner>(
            runtime.ServiceOrchestrator,
            "_batchRunner");
        Assert.IsType<WindowsProtectedPathSecurityVerifier>(
            GetPrivateField<IProtectedPathSecurityVerifier>(batch, "_securityVerifier"));
        RecoveryDirectoryEnumerator enumerator = GetPrivateField<RecoveryDirectoryEnumerator>(
            batch,
            "_enumerator");
        Assert.Equal(
            ProtectedPathSet.CreateProduction().RecoveryRecordsDirectory,
            GetPrivateField<string>(enumerator, "_recordsDirectory"));
    }

    [Fact]
    public void ConsentProductionComposition_UsesOnlyProductionSecurityReadinessAndPathClassifiers()
    {
        var client = new SessionIdentity("S-1-5-21-1", "S-1-5-5-1-2", 7);
        var broker = new SessionIdentity("S-1-5-21-1", "S-1-5-5-3-4", 7);
        using var identity = new ConsentBrokerBootstrapIdentity(
            client,
            broker,
            new SecurityIdentifier(client.LogonSid),
            new SecurityIdentifier(broker.AccountSid),
            new SafeAccessTokenHandle(new nint(1)));
        var session = new ConsentBrokerProductionSessionFactory().Create(
            identity,
            new RecordingLoggerFactory(),
            new SystemClock());

        Assert.IsType<WindowsBrokerConnectionAuthenticator>(session.Authenticator);
        Assert.IsType<FileReplayRegistry>(session.ReplayRegistry);
        Assert.Equal(TimeSpan.FromMinutes(1), session.DurationPolicy.Minimum);
        Assert.Equal(TimeSpan.FromHours(24), session.DurationPolicy.Maximum);
        var adapter = Assert.IsType<BrokerConsentSessionRuntime>(session.Runtime);
        BrokerRuntime runtime = adapter.Runtime;
        Assert.IsType<WindowsFolderLockService>(runtime.FolderLockService);
        var pathValidator = GetPrivateField<WindowsFolderPathValidator>(
            runtime.CommandProcessor,
            "_pathValidator");
        Assert.IsType<WindowsRepositoryPathClassifier>(
            GetPrivateField<IRepositoryRootClassifier>(pathValidator, "_repositoryClassifier"));
        Assert.IsType<WindowsSynchronizationPathClassifier>(
            GetPrivateField<ISynchronizationRootClassifier>(pathValidator, "_synchronizationClassifier"));
        RecoveryCreateLockGate gate = GetPrivateField<RecoveryCreateLockGate>(
            runtime.CommandProcessor,
            "_createLockGate");
        Assert.IsType<WindowsRecoveryReadinessStore>(
            GetPrivateField<IRecoveryReadinessReader>(gate, "_reader"));
        Assert.IsType<LockTaskScheduler>(
            GetPrivateField<ILockTaskScheduler>(runtime.LifecycleController, "_scheduler"));
    }

    [Fact]
    public void ConsentProgram_UsesProtectedLoggerAndDoesNotWriteConsentStatusToConsole()
    {
        string program = File.ReadAllText(Path.Combine(
            FindSolutionRoot(),
            "src",
            "FolderSessionLock.Broker",
            "Program.cs"));
        int consentStart = program.IndexOf(
            "if (options!.RunMode == BrokerRunMode.ConsentBroker)",
            StringComparison.Ordinal);
        int recoveryStart = program.IndexOf(
            "if (options.RunMode == BrokerRunMode.RecoveryService)",
            consentStart,
            StringComparison.Ordinal);
        string consentBranch = program[consentStart..recoveryStart];

        Assert.Contains("new WindowsProtectedLoggerFactory()", consentBranch, StringComparison.Ordinal);
        Assert.Contains("new ProductionConsentBrokerPipeRunner()", consentBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", consentBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("NullLogger", consentBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("Debug", consentBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryProgram_UsesProductionReadinessAndProtectedLoggerOnly()
    {
        string program = File.ReadAllText(Path.Combine(
            FindSolutionRoot(),
            "src",
            "FolderSessionLock.Broker",
            "Program.cs"));
        int serviceStart = program.IndexOf(
            "if (options.RunMode == BrokerRunMode.RecoveryService)",
            StringComparison.Ordinal);
        int onceStart = program.IndexOf(
            "Result<ILoggerFactory> recoveryLoggerResult =",
            StringComparison.Ordinal);
        Assert.True(serviceStart >= 0);
        Assert.True(onceStart > serviceStart);
        string serviceBranch = program[serviceStart..onceStart];

        Assert.Contains("new WindowsRecoveryServiceHost(", serviceBranch, StringComparison.Ordinal);
        Assert.Contains("new WindowsRecoveryServiceDispatcher()", serviceBranch, StringComparison.Ordinal);
        Assert.Contains("RunRecoveryServiceAsync", serviceBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", serviceBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("NullRecoveryServiceStatusReporter", serviceBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.CancelKeyPress", program, StringComparison.Ordinal);
        Assert.Contains("ProtectedLoggerMode.RecoveryOnce", program, StringComparison.Ordinal);
        Assert.Contains("ProtectedLoggerMode.RecoveryService", program, StringComparison.Ordinal);
        Assert.Contains("WindowsRecoveryReadinessStore.CreateProduction(clock)", program, StringComparison.Ordinal);
        Assert.Contains("BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE", program, StringComparison.Ordinal);
        Assert.Contains("return (int)RecoveryOnceExitCode.InternalFailure;", program, StringComparison.Ordinal);
        Assert.DoesNotContain("UnavailableRecoveryReadinessPublisher", program, StringComparison.Ordinal);
        Assert.DoesNotContain("NullLogger", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Debug", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddConsole", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddEventLog", program, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryOnceConsoleOutput_IsStructuredResultOnlyAndNotAProductionLogger()
    {
        string program = File.ReadAllText(Path.Combine(
            FindSolutionRoot(),
            "src",
            "FolderSessionLock.Broker",
            "Program.cs"));

        Assert.Contains(
            "Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(LoggerUnavailableSummary()))",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary))",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Console.WriteLine(exception", program, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Console.Error", program, StringComparison.Ordinal);
        Assert.DoesNotContain("ConsoleLogger", program, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerAssembly_DoesNotExposeArbitraryRecoveryExecutionOrPathConstructors()
    {
        Type assemblyMarker = typeof(BrokerCompositionRoot);
        Type[] exported = assemblyMarker.Assembly.GetExportedTypes();

        Assert.DoesNotContain(exported, type => type == typeof(RecoveryRecordAclCleanup));
        Assert.DoesNotContain(exported, type => type.Name is "RecoveryBatchRunner" or "RecoveryOnceRunner" or "RecoveryServiceOrchestrator");
        Assert.Empty(typeof(FileReplayRegistry).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(BrokerStartupOptions).GetProperties(),
            property => property.Name.Contains("Path", StringComparison.Ordinal)
                || property.Name.Contains("Service", StringComparison.Ordinal)
                || property.Name.Contains("Acl", StringComparison.Ordinal));
        Assert.Equal(
            ["ConsentBroker", "RecoveryService", "RecoveryOnce"],
            Enum.GetNames<BrokerRunMode>());
    }

    private static bool IsExecutableProject(string projectPath)
    {
        XDocument project = XDocument.Load(projectPath);
        string? outputType = project.Descendants("OutputType").SingleOrDefault()?.Value;
        return outputType is "Exe" or "WinExe";
    }

    private static string[] GetProjectReferences(string projectPath)
    {
        XDocument project = XDocument.Load(projectPath);
        return project.Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")!.Value)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
    }

    private static string FindSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FolderSessionLock.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("FolderSessionLock.sln was not found above the test output directory.");
    }

    private static T GetPrivateField<T>(object instance, string fieldName) =>
        (T)instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        internal List<LogEntry> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(string category, List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Add(new LogEntry(category, formatter(state, exception), exception));
    }

    private sealed record LogEntry(string Category, string Message, Exception? Exception);
}
