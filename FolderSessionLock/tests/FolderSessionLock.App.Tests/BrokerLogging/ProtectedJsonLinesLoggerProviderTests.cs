using System.Text;
using System.Text.Json;
using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.BrokerLogging.Tests;

public sealed class ProtectedJsonLinesLoggerProviderTests
{
    private static readonly DateTimeOffset Started =
        new DateTimeOffset(2026, 7, 22, 1, 2, 3, 123, TimeSpan.Zero).AddTicks(4567);
    private static readonly Guid InstanceId =
        Guid.Parse("11111111-2222-4333-8444-555555555555");

    [Fact]
    public void Log_WritesExactFourteenFieldUtf8LineAndIgnoresFormatterAndException()
    {
        var platform = new RecordingPlatform();
        DateTimeOffset now = Started;
        using var provider = new ProtectedJsonLinesLoggerProvider(
            ProtectedLoggerMode.ConsentBroker,
            InstanceId,
            platform,
            () => now,
            1234);
        Assert.True(provider.Initialize().IsSuccess);
        ILogger logger = provider.CreateLogger("sensitive.category");
        var context = new ProtectedLogContext(
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            Guid.Parse("99999999-8888-4777-8666-555555555555"),
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE);

        logger.Log(
            LogLevel.Information,
            new EventId(ProtectedLogEventCatalog.BrokerStarting.EventId, "TamperedName"),
            context,
            new InvalidOperationException("sensitive exception"),
            (_, _) => "sensitive formatter output");

        byte[] line = Assert.Single(platform.Files).Bytes.ToArray();
        Assert.InRange(line.Length, 1, ProtectedJsonLinesLoggerProvider.MaximumLineBytes);
        Assert.Equal((byte)'\n', line[^1]);
        Assert.False(line.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.DoesNotContain((byte)'\r', line);
        using JsonDocument document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
        JsonProperty[] properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal(
            [
                "schemaVersion",
                "timestampUtc",
                "sequence",
                "level",
                "eventId",
                "eventName",
                "mode",
                "component",
                "processId",
                "instanceId",
                "requestId",
                "taskId",
                "errorCode",
                "message",
            ],
            properties.Select(property => property.Name));
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-07-22T01:02:03.1234567Z", root.GetProperty("timestampUtc").GetString());
        Assert.Equal(1, root.GetProperty("sequence").GetInt64());
        Assert.Equal("Information", root.GetProperty("level").GetString());
        Assert.Equal(1001, root.GetProperty("eventId").GetInt32());
        Assert.Equal("BrokerStarting", root.GetProperty("eventName").GetString());
        Assert.Equal("ConsentBroker", root.GetProperty("mode").GetString());
        Assert.Equal("BrokerBootstrap", root.GetProperty("component").GetString());
        Assert.Equal(1234U, root.GetProperty("processId").GetUInt32());
        Assert.Equal(InstanceId.ToString("D"), root.GetProperty("instanceId").GetString());
        Assert.Equal(context.RequestId!.Value.ToString("D"), root.GetProperty("requestId").GetString());
        Assert.Equal(context.TaskId!.Value.ToString("D"), root.GetProperty("taskId").GetString());
        Assert.Equal(context.ErrorCode, root.GetProperty("errorCode").GetString());
        Assert.Equal(
            ProtectedLogEventCatalog.BrokerStarting.Message,
            root.GetProperty("message").GetString());
        string text = Encoding.UTF8.GetString(line);
        Assert.DoesNotContain("sensitive", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TamperedName", text, StringComparison.Ordinal);
        Assert.Equal(1, platform.FlushCount);
    }

    [Fact]
    public void Log_SequenceIsPerFileAndRotationUsesSizeAndUtcDay()
    {
        var platform = new RecordingPlatform();
        DateTimeOffset now = Started;
        using var provider = new ProtectedJsonLinesLoggerProvider(
            ProtectedLoggerMode.RecoveryService,
            InstanceId,
            platform,
            () => now,
            42,
            maximumFileBytes: 700);
        Assert.True(provider.Initialize().IsSuccess);
        ILogger logger = provider.CreateLogger("test");

        Write(logger, ProtectedLogEventCatalog.ReadinessStateChanged);
        Write(logger, ProtectedLogEventCatalog.ReadinessStateChanged);
        now = Started.AddDays(1);
        Write(logger, ProtectedLogEventCatalog.ReadinessStateChanged);

        Assert.Equal(3, platform.Files.Count);
        Assert.EndsWith("-0000.jsonl", platform.Files[0].LeafName, StringComparison.Ordinal);
        Assert.EndsWith("-0001.jsonl", platform.Files[1].LeafName, StringComparison.Ordinal);
        Assert.EndsWith("-0002.jsonl", platform.Files[2].LeafName, StringComparison.Ordinal);
        Assert.All(platform.Files, file => Assert.Equal(1, ReadSequences(file).Single()));
        Assert.Equal(3, platform.FlushCount);
    }

    [Theory]
    [InlineData(ProtectedLoggerMode.ConsentBroker, "consent-broker")]
    [InlineData(ProtectedLoggerMode.RecoveryService, "recovery-service")]
    [InlineData(ProtectedLoggerMode.RecoveryOnce, "recovery-once")]
    public void Initialize_UsesExactModeAndFileName(
        ProtectedLoggerMode mode,
        string expectedDirectory)
    {
        var platform = new RecordingPlatform();
        using var provider = new ProtectedJsonLinesLoggerProvider(
            mode,
            InstanceId,
            platform,
            () => Started,
            1234);

        Assert.True(provider.Initialize().IsSuccess);

        RecordedFile file = Assert.Single(platform.Files);
        Assert.Equal(mode, file.Mode);
        Assert.Equal(expectedDirectory, WindowsProtectedLogFilePlatform.ModeDirectoryName(mode));
        Assert.Equal(
            "20260722T0102031234567Z-1234-11111111-2222-4333-8444-555555555555-0000.jsonl",
            file.LeafName);
    }

    [Fact]
    public void Log_IgnoresUnsupportedLevelsAndUnknownEvents()
    {
        var platform = new RecordingPlatform();
        using var provider = new ProtectedJsonLinesLoggerProvider(
            ProtectedLoggerMode.RecoveryOnce,
            InstanceId,
            platform,
            () => Started,
            7);
        Assert.True(provider.Initialize().IsSuccess);
        ILogger logger = provider.CreateLogger("test");

        logger.Log(LogLevel.Debug, new EventId(1001), new ProtectedLogContext(), null, (_, _) => "debug");
        logger.Log(LogLevel.Information, new EventId(999999), new ProtectedLogContext(), null, (_, _) => "unknown");

        Assert.Empty(Assert.Single(platform.Files).Bytes);
        Assert.Equal(0, platform.FlushCount);
        Assert.False(provider.IsPermanentlyFailed);
    }

    [Fact]
    public void Log_SchemaAcceptsExactSchedulerLoopErrorCode()
    {
        var platform = new RecordingPlatform();
        using var provider = new ProtectedJsonLinesLoggerProvider(
            ProtectedLoggerMode.ConsentBroker,
            InstanceId,
            platform,
            () => Started,
            7);
        Assert.True(provider.Initialize().IsSuccess);
        ILogger logger = provider.CreateLogger("test");

        logger.Log(
            LogLevel.Error,
            new EventId(
                ProtectedLogEventCatalog.SchedulerStopped.EventId,
                ProtectedLogEventCatalog.SchedulerStopped.EventName),
            new ProtectedLogContext(ErrorCode: "lock_task.scheduler.loop.exception"),
            null,
            (_, _) => "ignored");

        Assert.False(provider.IsPermanentlyFailed);
        byte[] line = Assert.Single(platform.Files).Bytes.ToArray();
        using JsonDocument document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
        JsonElement root = document.RootElement;
        Assert.Equal("Error", root.GetProperty("level").GetString());
        Assert.Equal("Scheduler", root.GetProperty("component").GetString());
        Assert.Equal(
            "lock_task.scheduler.loop.exception",
            root.GetProperty("errorCode").GetString());
        Assert.Equal(
            "The lock task scheduler loop terminated unexpectedly.",
            root.GetProperty("message").GetString());
    }

    [Fact]
    public void Log_SchemaRejectsDeprecatedSchedulerLoopErrorCode()
    {
        var platform = new RecordingPlatform();
        using var provider = new ProtectedJsonLinesLoggerProvider(
            ProtectedLoggerMode.ConsentBroker,
            InstanceId,
            platform,
            () => Started,
            7);
        Assert.True(provider.Initialize().IsSuccess);
        ILogger logger = provider.CreateLogger("test");

        logger.Log(
            LogLevel.Error,
            new EventId(
                ProtectedLogEventCatalog.SchedulerStopped.EventId,
                ProtectedLogEventCatalog.SchedulerStopped.EventName),
            new ProtectedLogContext(ErrorCode: "lock_task_scheduler.loop.exception"),
            null,
            (_, _) => "ignored");

        Assert.True(provider.IsPermanentlyFailed);
        Assert.Empty(Assert.Single(platform.Files).Bytes);
        Assert.Equal(0, platform.FlushCount);
    }

    [Fact]
    public void WriteFailure_PermanentlyStopsTheProvider()
    {
        var platform = new RecordingPlatform { FailWrite = true };
        using var provider = new ProtectedJsonLinesLoggerProvider(
            ProtectedLoggerMode.ConsentBroker,
            InstanceId,
            platform,
            () => Started,
            11);
        Assert.True(provider.Initialize().IsSuccess);
        ILogger logger = provider.CreateLogger("test");

        Write(logger, ProtectedLogEventCatalog.BrokerStarting);
        platform.FailWrite = false;
        Write(logger, ProtectedLogEventCatalog.BrokerStarting);

        Assert.True(provider.IsPermanentlyFailed);
        Assert.Equal(1, platform.WriteCount);
        Assert.Equal(0, platform.FlushCount);
    }

    [Fact]
    public void Redactor_UsesExactDomainSeparatedLowercaseSha256()
    {
        const string path = @"C:\Folder\Target";

        string hash = ProtectedLogRedactor.HashPath(path);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(
            "ad281748e26228620b6f391f486bbc0338280e3821fbea808a203804df35f8f4",
            hash);
        Assert.DoesNotContain("Folder", hash, StringComparison.Ordinal);
    }

    private static void Write(ILogger logger, ProtectedLogEvent logEvent) => logger.Log(
        LogLevel.Information,
        new EventId(logEvent.EventId, logEvent.EventName),
        new ProtectedLogContext(),
        null,
        (_, _) => "ignored");

    private static long[] ReadSequences(RecordedFile file) => Encoding.UTF8
        .GetString(file.Bytes.ToArray())
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64())
        .ToArray();

    private sealed class RecordingPlatform : IProtectedLogFilePlatform
    {
        internal List<RecordedFile> Files { get; } = [];
        internal bool FailWrite { get; set; }
        internal int WriteCount { get; private set; }
        internal int FlushCount { get; private set; }

        public Result<IProtectedLogFile> CreateNew(
            ProtectedLoggerMode mode,
            string leafName)
        {
            var file = new RecordedFile(mode, leafName);
            Files.Add(file);
            return Result<IProtectedLogFile>.Success(file);
        }

        public Result Write(
            IProtectedLogFile file,
            ReadOnlyMemory<byte> bytes,
            long offset)
        {
            WriteCount++;
            if (FailWrite)
            {
                return Failure();
            }

            var recorded = (RecordedFile)file;
            Assert.Equal(recorded.Bytes.Count, offset);
            recorded.Bytes.AddRange(bytes.ToArray());
            return Result.Success();
        }

        public Result Flush(IProtectedLogFile file)
        {
            FlushCount++;
            return Result.Success();
        }

        private static Result Failure() => Result.Failure(new Error(
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            ErrorCategory.UnrecoverableError));
    }

    private sealed class RecordedFile(
        ProtectedLoggerMode mode,
        string leafName) : IProtectedLogFile
    {
        internal ProtectedLoggerMode Mode { get; } = mode;
        internal List<byte> Bytes { get; } = [];
        public string LeafName { get; } = leafName;
        public void Dispose()
        {
        }
    }
}
