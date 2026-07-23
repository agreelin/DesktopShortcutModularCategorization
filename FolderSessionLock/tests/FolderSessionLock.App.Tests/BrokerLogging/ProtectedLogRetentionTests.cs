using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.BrokerLogging.Tests;

public sealed class ProtectedLogRetentionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cleanup_DeletesExpiredThenStablePerModeAndGlobalOrder()
    {
        var platform = new FakePlatform(
        [
            Artifact(ProtectedLoggerMode.ConsentBroker, "old.jsonl", Now.AddDays(-15), 1),
            Artifact(ProtectedLoggerMode.ConsentBroker, "b.jsonl", Now.AddDays(-2), 4),
            Artifact(ProtectedLoggerMode.ConsentBroker, "a.jsonl", Now.AddDays(-2), 4),
            Artifact(ProtectedLoggerMode.RecoveryService, "service.jsonl", Now.AddDays(-1), 4),
        ]);
        var retention = new ProtectedLogRetention(
            platform,
            maximumClosedFilesPerMode: 2,
            maximumTotalBytes: 8);

        Result result = retention.Cleanup(Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(["old.jsonl", "a.jsonl"], platform.Deleted);
    }

    [Fact]
    public void Cleanup_NeverDeletesActiveOrUnsafeArtifacts()
    {
        var platform = new FakePlatform(
        [
            Artifact(
                ProtectedLoggerMode.ConsentBroker,
                "active.jsonl",
                Now.AddDays(-20),
                100,
                isActive: true),
            Artifact(
                ProtectedLoggerMode.ConsentBroker,
                "unsafe.jsonl",
                Now.AddDays(-20),
                100,
                isSafe: false),
            Artifact(
                ProtectedLoggerMode.ConsentBroker,
                "closed.jsonl",
                Now.AddDays(-20),
                1),
        ]);
        var retention = new ProtectedLogRetention(platform, 32, 1000);

        Result result = retention.Cleanup(Now);

        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_PROTECTED_LOG_ARTIFACT_INVALID, result.Error!.Code);
        Assert.Equal(["closed.jsonl"], platform.Deleted);
    }

    [Fact]
    public void Cleanup_FailsWhenHardLimitCannotBeReachedWithoutDeletingActiveFiles()
    {
        var platform = new FakePlatform(
        [
            Artifact(
                ProtectedLoggerMode.RecoveryService,
                "active.jsonl",
                Now,
                11,
                isActive: true),
        ]);
        var retention = new ProtectedLogRetention(platform, 32, 10);

        Result result = retention.Cleanup(Now);

        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE, result.Error!.Code);
        Assert.Empty(platform.Deleted);
    }

    [Fact]
    public void FileNameParser_AcceptsOnlyTheExactCanonicalShape()
    {
        const string valid =
            "20260722T0102031234567Z-1234-11111111-2222-4333-8444-555555555555-0000.jsonl";

        Assert.True(ProtectedLogFileName.TryParse(
            valid,
            out DateTimeOffset started,
            out uint processId,
            out Guid instanceId,
            out int rotation));
        Assert.Equal(Now.AddMinutes(2).AddSeconds(3).AddMilliseconds(123).AddTicks(4567), started);
        Assert.Equal(1234U, processId);
        Assert.Equal(Guid.Parse("11111111-2222-4333-8444-555555555555"), instanceId);
        Assert.Equal(0, rotation);
        Assert.False(ProtectedLogFileName.TryParse(valid.ToLowerInvariant(), out _, out _, out _, out _));
        Assert.False(ProtectedLogFileName.TryParse(valid.Replace("-0000", "-10000", StringComparison.Ordinal), out _, out _, out _, out _));
        Assert.False(ProtectedLogFileName.TryParse(valid.Replace("-1234-", "-01234-", StringComparison.Ordinal), out _, out _, out _, out _));
    }

    [Fact]
    public void ProductionFactory_RunsRetentionBeforeCreatingTheFirstLogFile()
    {
        var calls = new List<string>();
        var retention = new MutableRetentionPlatform(calls);
        var files = new RecordingLogPlatform(calls);
        var factory = new WindowsProtectedLoggerFactory(
            () => files,
            () => retention,
            () => Now);

        Result<ILoggerFactory> result = factory.Create(
            ProtectedLoggerMode.RecoveryService,
            Guid.Parse("11111111-2222-4333-8444-555555555555"));

        Assert.True(result.IsSuccess, result.Error?.Code);
        using ILoggerFactory logger = result.Value;
        Assert.Equal(["retention", "create"], calls);
    }

    [Fact]
    public void ProductionFactory_InvalidRetentionArtifactFailsClosedBeforeFileCreation()
    {
        var calls = new List<string>();
        var retention = new MutableRetentionPlatform(calls)
        {
            ArtifactInvalid = true,
        };
        var files = new RecordingLogPlatform(calls);
        var factory = new WindowsProtectedLoggerFactory(
            () => files,
            () => retention,
            () => Now);

        Result<ILoggerFactory> result = factory.Create(
            ProtectedLoggerMode.ConsentBroker,
            Guid.Parse("11111111-2222-4333-8444-555555555555"));

        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE, result.Error!.Code);
        Assert.Equal(["retention"], calls);
        Assert.Equal(0, files.CreateCount);
    }

    [Fact]
    public void Provider_RunsHardLimitCheckBeforeEveryRotatedFile()
    {
        int preCreateCount = 0;
        var files = new RecordingLogPlatform([]);
        using var provider = new ProtectedJsonLinesLoggerProvider(
            ProtectedLoggerMode.ConsentBroker,
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            files,
            () => Now,
            processId: 7,
            maximumFileBytes: 512,
            beforeFileCreate: () =>
            {
                preCreateCount++;
                return Result.Success();
            });
        Assert.True(provider.Initialize().IsSuccess);
        ILogger logger = provider.CreateLogger("test");

        for (int index = 0; index < 20 && files.CreateCount < 2; index++)
        {
            ProtectedLogEvent entry = ProtectedLogEventCatalog.BrokerStarting;
            logger.Log(
                LogLevel.Information,
                new EventId(entry.EventId, entry.EventName),
                new ProtectedLogContext(),
                null,
                static (_, _) => string.Empty);
        }

        Assert.Equal(2, files.CreateCount);
        Assert.Equal(files.CreateCount, preCreateCount);
    }

    [Fact]
    public void MaintenanceFailurePermanentlyFailsTheReturnedLoggerFactory()
    {
        var retention = new MutableRetentionPlatform([]);
        var factory = new WindowsProtectedLoggerFactory(
            () => new RecordingLogPlatform([]),
            () => retention,
            () => Now);
        Result<ILoggerFactory> created = factory.Create(
            ProtectedLoggerMode.RecoveryService,
            Guid.Parse("11111111-2222-4333-8444-555555555555"));
        Assert.True(created.IsSuccess);
        using ILoggerFactory logger = created.Value;
        retention.FailEnumeration = true;

        Result maintenance = Assert.IsAssignableFrom<IProtectedLogMaintenance>(logger)
            .RunMaintenance();

        Assert.True(maintenance.IsFailure);
        Assert.True(Assert.IsAssignableFrom<IProtectedLoggerHealth>(logger).IsPermanentlyFailed);
    }

    private static ProtectedLogArtifact Artifact(
        ProtectedLoggerMode mode,
        string leafName,
        DateTimeOffset lastWriteUtc,
        long length,
        bool isActive = false,
        bool isSafe = true) => new(
            mode,
            leafName,
            lastWriteUtc,
            length,
            isActive,
            isSafe,
            new FakeFile());

    private sealed class FakePlatform(IEnumerable<ProtectedLogArtifact> artifacts)
        : IProtectedLogRetentionPlatform
    {
        private readonly ProtectedLogArtifact[] _artifacts = artifacts.ToArray();

        internal List<string> Deleted { get; } = [];

        public Result<IReadOnlyList<ProtectedLogArtifact>> Enumerate(ProtectedLoggerMode mode) =>
            Result<IReadOnlyList<ProtectedLogArtifact>>.Success(
                _artifacts.Where(artifact => artifact.Mode == mode).ToArray());

        public Result Delete(ProtectedLogArtifact artifact)
        {
            Deleted.Add(artifact.LeafName);
            return Result.Success();
        }
    }

    private sealed class FakeFile : IProtectedLogRetentionFile
    {
        public void Dispose()
        {
        }
    }

    private sealed class MutableRetentionPlatform(List<string> calls)
        : IProtectedLogRetentionPlatform
    {
        private bool _recorded;

        internal bool ArtifactInvalid { get; set; }
        internal bool FailEnumeration { get; set; }

        public Result<IReadOnlyList<ProtectedLogArtifact>> Enumerate(ProtectedLoggerMode mode)
        {
            if (!_recorded)
            {
                calls.Add("retention");
                _recorded = true;
            }

            if (FailEnumeration)
            {
                return Result<IReadOnlyList<ProtectedLogArtifact>>.Failure(new Error(
                    BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
                    BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
                    ErrorCategory.UnrecoverableError));
            }

            return Result<IReadOnlyList<ProtectedLogArtifact>>.Success(
                ArtifactInvalid && mode == ProtectedLoggerMode.ConsentBroker
                    ? [Artifact(mode, "invalid", Now, 0, isSafe: false)]
                    : []);
        }

        public Result Delete(ProtectedLogArtifact artifact) => Result.Success();
    }

    private sealed class RecordingLogPlatform(List<string> calls) : IProtectedLogFilePlatform
    {
        internal int CreateCount { get; private set; }

        public Result<IProtectedLogFile> CreateNew(
            ProtectedLoggerMode mode,
            string leafName)
        {
            calls.Add("create");
            CreateCount++;
            return Result<IProtectedLogFile>.Success(new RecordingLogFile(leafName));
        }

        public Result Write(
            IProtectedLogFile file,
            ReadOnlyMemory<byte> bytes,
            long offset) => Result.Success();

        public Result Flush(IProtectedLogFile file) => Result.Success();
    }

    private sealed class RecordingLogFile(string leafName) : IProtectedLogFile
    {
        public string LeafName { get; } = leafName;
        public void Dispose()
        {
        }
    }
}
