using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Broker.Recovery.Tests;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Transport.Tests;

public sealed class ReplayRegistryTests
{
    [Fact]
    public void RecoveryEvidenceProvider_UsesOnlyExplicitCurrentProcessRequestMapping()
    {
        var registry = new RecoveryTaskRegistry();
        var provider = new RecoveryReplaySideEffectEvidenceProvider(registry);
        Guid requestId = Guid.NewGuid();
        Guid taskId = Guid.NewGuid();

        Assert.Equal(ReplaySideEffectEvidence.Unknown, provider.Inspect(requestId));
        Assert.True(registry.BeginRequest(requestId, taskId));
        Assert.Equal(ReplaySideEffectEvidence.None, provider.Inspect(requestId));
        Assert.True(registry.TryAdd(RecoveryTestData.Applied() with
        {
            RecordId = Guid.NewGuid(),
            TaskId = taskId,
        }));
        Assert.Equal(ReplaySideEffectEvidence.RecoveryRecordPresent, provider.Inspect(requestId));

        var newProcessRegistry = new RecoveryTaskRegistry();
        Assert.Equal(
            ReplaySideEffectEvidence.Unknown,
            new RecoveryReplaySideEffectEvidenceProvider(newProcessRegistry).Inspect(requestId));
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 19, 16, 30, 0, TimeSpan.Zero);
    private static readonly Guid RequestId = Guid.ParseExact("11111111-2222-3333-4444-555555555555", "D");
    private static readonly SessionIdentity Identity = new(
        "S-1-5-21-100-200-300-400",
        "S-1-5-5-100-200",
        1);

    [Fact]
    public void ReplayKeyUsesExactCanonicalSha256AndHidesSidAndRequestId()
    {
        string key = FileReplayRegistry.CreateReplayKey(Identity, RequestId);

        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.DoesNotContain("S-1-5", key, StringComparison.Ordinal);
        Assert.DoesNotContain(RequestId.ToString("D"), key, StringComparison.Ordinal);
        Assert.Equal(
            "fe2c607514bece10e07c25fee2e52f083bfdf0d1c230918499241b3fc3484804",
            key);
    }

    [Fact]
    public async Task AcquireCreatesExactSchemaAndConcurrentOwnerIsInProgress()
    {
        await using Context context = Context.Create(new FixedEvidence(ReplaySideEffectEvidence.None));
        using var start = new ManualResetEventSlim(false);
        Task<ReplayAcquireResult>[] attempts = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await context.Registry.AcquireAsync(
                    context.Client,
                    RequestId,
                    BrokerCommand.GetStatus);
            }))
            .ToArray();
        start.Set();
        ReplayAcquireResult[] results = await Task.WhenAll(attempts);
        ReplayAcquireResult first = Assert.Single(results, result => result.IsSuccess);
        ReplayAcquireResult second = Assert.Single(results, result => !result.IsSuccess);
        string file = Assert.Single(Directory.EnumerateFiles(context.Root, "*.fsrr"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));

        Assert.True(first.IsSuccess);
        Assert.Equal(
            ["schemaVersion",
                "replayKeySha256",
                "requestId",
                "command",
                "state",
                "ownerProcessId",
                "ownerProcessStartUtc",
                "ownerNonce",
                "connectionId",
                "createdUtc",
                "lastUpdatedUtc",
                "leaseExpiresUtc",
                "retentionExpiresUtc",
                "terminalCode"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Handshaking", document.RootElement.GetProperty("state").GetString());
        Assert.False(second.IsSuccess);
        Assert.Equal(BrokerErrorCodes.FSL_E_REQUEST_IN_PROGRESS, second.Error!.Code);
        Assert.True(second.Error.Retryable);
    }

    [Fact]
    public async Task AcquireRecoversRealAbandonedMutexAndReleasesOwnership()
    {
        string mutexName = $"Local\\FolderSessionLock.Tests.{Guid.NewGuid():N}";
        using var mutex = new Mutex(false, mutexName);
        using var owned = new ManualResetEventSlim(false);
        var owner = new Thread(() =>
        {
            mutex.WaitOne();
            owned.Set();
        });
        owner.Start();
        Assert.True(owned.Wait(TimeSpan.FromSeconds(5)));
        owner.Join();
        await using Context context = Context.Create(
            new FixedEvidence(ReplaySideEffectEvidence.None),
            mutexName);

        ReplayAcquireResult result = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);

        Assert.True(result.IsSuccess);
        Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(5)));
        mutex.ReleaseMutex();
    }

    [Fact]
    public async Task ExpiredLeaseWithLiveOwnerRemainsInProgress()
    {
        await using Context context = Context.Create(new FixedEvidence(ReplaySideEffectEvidence.None));
        ReplayAcquireResult first = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);
        Assert.True(first.IsSuccess);
        MutateProperty(
            context.Root,
            "leaseExpiresUtc",
            context.Clock.UtcNow.AddSeconds(-1).ToString(BrokerProtocolConstants.UtcTimestampFormat));

        ReplayAcquireResult result = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);

        Assert.Equal(BrokerErrorCodes.FSL_E_REQUEST_IN_PROGRESS, result.Error!.Code);
        Assert.Equal("Handshaking", ReadState(context.Root));
    }

    [Fact]
    public async Task LeaseRenewalAndTimeoutConstantsAreExact()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), BrokerPipeConnection.ClientHelloTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), BrokerPipeConnection.RequestTimeWindow);
        Assert.Equal(TimeSpan.FromMinutes(5), BrokerPipeConnection.MaximumExecutionDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), FileReplayRegistry.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(20), FileReplayRegistry.RenewalPeriod);
        Assert.Equal(TimeSpan.FromMinutes(5), FileReplayRegistry.MaximumExecutionDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), FileReplayRegistry.TerminalRetention);
        await using Context context = Context.Create(new FixedEvidence(ReplaySideEffectEvidence.None));
        ReplayAcquireResult acquired = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);
        context.Clock.UtcNow = context.Clock.UtcNow.Add(FileReplayRegistry.RenewalPeriod);

        BrokerError? error = await context.Registry.RenewAsync(acquired.Lease!);

        Assert.Null(error);
        Assert.Equal(context.Clock.UtcNow, ReadTimestamp(context.Root, "lastUpdatedUtc"));
        Assert.Equal(
            context.Clock.UtcNow.Add(FileReplayRegistry.LeaseDuration),
            ReadTimestamp(context.Root, "leaseExpiresUtc"));
    }

    [Fact]
    public async Task TerminalStateRetainsTenMinutesThenAllowsNewOwner()
    {
        await using Context context = Context.Create(new FixedEvidence(ReplaySideEffectEvidence.None));
        ReplayAcquireResult first = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);
        Assert.Null(await context.Registry.MarkChallengeIssuedAsync(first.Lease!, Guid.NewGuid()));
        Assert.Null(await context.Registry.MarkExecutingAsync(first.Lease!));
        Assert.Null(await context.Registry.CompleteAsync(first.Lease!, ReplayState.Succeeded, null));

        ReplayAcquireResult retained = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);
        context.Clock.UtcNow = context.Clock.UtcNow.Add(FileReplayRegistry.TerminalRetention).AddTicks(1);
        ReplayAcquireResult replaced = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);

        Assert.Equal(BrokerErrorCodes.FSL_E_REPLAY_DETECTED, retained.Error!.Code);
        Assert.False(retained.Error.Retryable);
        Assert.True(replaced.IsSuccess);
        Assert.Equal("Handshaking", ReadState(context.Root));
    }

    [Fact]
    public async Task ConcurrentExpiredTerminalCleanupCreatesOneNewOwner()
    {
        await using Context context = Context.Create(new FixedEvidence(ReplaySideEffectEvidence.None));
        ReplayAcquireResult first = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);
        Assert.Null(await context.Registry.CompleteAsync(first.Lease!, ReplayState.Succeeded, null));
        context.Clock.UtcNow = context.Clock.UtcNow.Add(FileReplayRegistry.TerminalRetention).AddTicks(1);
        using var start = new ManualResetEventSlim(false);
        Task<ReplayAcquireResult>[] attempts = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await context.Registry.AcquireAsync(
                    context.Client,
                    RequestId,
                    BrokerCommand.GetStatus);
            }))
            .ToArray();
        start.Set();

        ReplayAcquireResult[] results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.IsSuccess);
        ReplayAcquireResult rejected = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal(BrokerErrorCodes.FSL_E_REQUEST_IN_PROGRESS, rejected.Error!.Code);
        Assert.Equal("Handshaking", ReadState(context.Root));
        Assert.Single(Directory.EnumerateFiles(context.Root, "*.fsrr"));
    }

    [Fact]
    public async Task ExpiredDeadOwnerWithUnknownEvidenceBecomesRecoveryRequiredForever()
    {
        await using Context context = Context.Create(new FixedEvidence(ReplaySideEffectEvidence.Unknown));
        ReplayAcquireResult first = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.CreateLock);
        Assert.True(first.IsSuccess);
        MutateOwnerAsDeadAndExpired(context.Root, context.Clock.UtcNow.AddSeconds(-1));

        ReplayAcquireResult result = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.CreateLock);
        context.Clock.UtcNow = context.Clock.UtcNow.AddDays(365);
        ReplayAcquireResult later = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.CreateLock);

        Assert.Equal(BrokerErrorCodes.FSL_E_REPLAY_DETECTED, result.Error!.Code);
        Assert.Equal("RecoveryRequired", ReadState(context.Root));
        Assert.Equal(BrokerErrorCodes.FSL_E_REPLAY_DETECTED, later.Error!.Code);
    }

    [Fact]
    public async Task ExpiredDeadOwnerWithNoEvidenceBecomesAbandoned()
    {
        await using Context context = Context.Create(new FixedEvidence(ReplaySideEffectEvidence.None));
        ReplayAcquireResult first = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);
        Assert.True(first.IsSuccess);
        MutateOwnerAsDeadAndExpired(context.Root, context.Clock.UtcNow.AddSeconds(-1));

        ReplayAcquireResult result = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);

        Assert.Equal(BrokerErrorCodes.FSL_E_REPLAY_DETECTED, result.Error!.Code);
        Assert.Equal("Abandoned", ReadState(context.Root));
        Assert.Equal(BrokerErrorCodes.FSL_E_HANDSHAKE_EXPIRED, ReadProperty(context.Root, "terminalCode"));
    }

    [Fact]
    public async Task ReusedOwnerProcessIdWithDifferentStartTimeIsNotTreatedAsLiveOwner()
    {
        await using Context context = Context.Create(new FixedEvidence(ReplaySideEffectEvidence.None));
        ReplayAcquireResult first = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);
        Assert.True(first.IsSuccess);
        MutateProperty(
            context.Root,
            "ownerProcessStartUtc",
            "2000-01-01T00:00:00.0000000Z");
        MutateProperty(
            context.Root,
            "leaseExpiresUtc",
            context.Clock.UtcNow.AddSeconds(-1).ToString(BrokerProtocolConstants.UtcTimestampFormat));

        ReplayAcquireResult result = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);

        Assert.Equal(BrokerErrorCodes.FSL_E_REPLAY_DETECTED, result.Error!.Code);
        Assert.Equal("Abandoned", ReadState(context.Root));
    }

    [Fact]
    public async Task NonOwnerCannotUpdateRecord()
    {
        await using Context context = Context.Create(new FixedEvidence(ReplaySideEffectEvidence.None));
        ReplayAcquireResult first = await context.Registry.AcquireAsync(
            context.Client,
            RequestId,
            BrokerCommand.GetStatus);
        MutateProperty(context.Root, "ownerNonce", Guid.NewGuid().ToString("D"));

        BrokerError? error = await context.Registry.MarkExecutingAsync(first.Lease!);

        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED, error!.Code);
        Assert.Equal("Handshaking", ReadState(context.Root));
    }

    [Fact]
    public void ConsentArgumentsRequireExactOrderNamesValuesAndFormats()
    {
        string[] valid =
        [
            "--mode",
            "consent-broker",
            "--pipe-name",
            "FolderSessionLock.Broker.v1",
            "--session-id",
            "1",
            "--request-id",
            "a0b1c2d3-e4f5-4678-9123-abcdefabcdef",
            "--client-process-id",
            "1234",
            "--client-process-creation-filetime",
            "133970112000000000",
        ];

        Assert.True(BrokerConsentOptions.TryParse(valid, out BrokerConsentOptions? options));
        Assert.Equal(1U, options!.SessionId);
        Assert.Equal(1234U, options.ClientProcessId);
        Assert.Equal(133970112000000000UL, options.ClientProcessCreationFileTime);
        Assert.False(BrokerConsentOptions.TryParse(Changed(valid, 3, "foldersessionlock.broker.v1"), out _));
        Assert.False(BrokerConsentOptions.TryParse(
            Changed(valid, 7, valid[7].ToUpperInvariant()),
            out _));
        Assert.False(BrokerConsentOptions.TryParse(Changed(valid, 9, "01234"), out _));
        Assert.False(BrokerConsentOptions.TryParse(Changed(valid, 11, "0"), out _));
        Assert.False(BrokerConsentOptions.TryParse(valid[..^1], out _));
    }

    private static string[] Changed(string[] source, int index, string value)
    {
        string[] changed = (string[])source.Clone();
        changed[index] = value;
        return changed;
    }

    private static void MutateOwnerAsDeadAndExpired(string root, DateTimeOffset leaseExpiresUtc)
    {
        MutateProperty(root, "ownerProcessId", 4_000_000_000U);
        MutateProperty(root, "ownerProcessStartUtc", "2000-01-01T00:00:00.0000000Z");
        MutateProperty(root, "leaseExpiresUtc", leaseExpiresUtc.ToString(BrokerProtocolConstants.UtcTimestampFormat));
    }

    private static void MutateProperty(string root, string name, object value)
    {
        string file = Assert.Single(Directory.EnumerateFiles(root, "*.fsrr"));
        JsonObject node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        node[name] = JsonValue.Create(value);
        File.WriteAllText(file, node.ToJsonString());
    }

    private static string ReadState(string root) => ReadProperty(root, "state")!;

    private static string? ReadProperty(string root, string name)
    {
        string file = Assert.Single(Directory.EnumerateFiles(root, "*.fsrr"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
        JsonElement value = document.RootElement.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static DateTimeOffset ReadTimestamp(string root, string name) =>
        DateTimeOffset.ParseExact(
            ReadProperty(root, name)!,
            BrokerProtocolConstants.UtcTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed class Context : IAsyncDisposable
    {
        private Context(
            string root,
            MutableClock clock,
            FileReplayRegistry registry,
            BrokerAuthenticatedClient client)
        {
            Root = root;
            Clock = clock;
            Registry = registry;
            Client = client;
        }

        internal string Root { get; }
        internal MutableClock Clock { get; }
        internal FileReplayRegistry Registry { get; }
        internal BrokerAuthenticatedClient Client { get; }

        internal static Context Create(
            IReplaySideEffectEvidenceProvider evidence,
            string? mutexName = null)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "FolderSessionLock.Tests",
                Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(root);
            var clock = new MutableClock(Now);
            var registry = new FileReplayRegistry(
                root,
                mutexName ?? $"Local\\FolderSessionLock.Tests.{Guid.NewGuid():N}",
                clock,
                evidence);
            using Process process = Process.GetCurrentProcess();
            var client = new BrokerAuthenticatedClient(
                checked((uint)process.Id),
                process.StartTime.ToUniversalTime(),
                Identity,
                Identity);
            return new Context(root, clock, registry, client);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            new(Task.Delay(delay, cancellationToken));
    }

    private sealed class FixedEvidence(ReplaySideEffectEvidence evidence) : IReplaySideEffectEvidenceProvider
    {
        public ReplaySideEffectEvidence Inspect(Guid requestId) => evidence;
    }
}
