using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Broker.Transport;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Core.Services;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class BrokerCommandProcessorTests
{
    [Fact]
    public async Task CreateStatusAndRemove_UseRecoveryRegistryAndReturnExactProtocolResults()
    {
        using ProcessorContext context = ProcessorContext.Create();
        Guid taskId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        string targetPath = Path.Combine(context.RecordsDirectory, "target");
        Directory.CreateDirectory(targetPath);
        var createRequest = Request(
            BrokerCommand.CreateLock,
            new CreateLockRequest(taskId, targetPath, 120_000));

        BrokerExecutionOutcome created = await context.Processor.ProcessAsync(
            createRequest,
            BrokerExecutionContext.OrdinaryUi);
        BrokerExecutionOutcome replay = await context.Processor.ProcessAsync(
            createRequest with { RequestId = Guid.NewGuid() },
            BrokerExecutionContext.OrdinaryUi);

        Assert.Equal(BrokerExecutionEffect.Succeeded, created.Effect);
        CreateLockResult createResult = Assert.IsType<CreateLockResult>(created.Response.Result);
        Assert.Equal(LockTaskStatus.Active, createResult.Status);
        Assert.False(createResult.IdempotentReplay);
        Assert.Single(Directory.EnumerateFiles(context.RecordsDirectory, "*.fslr"));
        Assert.Equal(1, context.FolderLockService.CreateCount);
        Assert.True(Assert.IsType<CreateLockResult>(replay.Response.Result).IdempotentReplay);
        Assert.Equal(1, context.FolderLockService.CreateCount);

        BrokerExecutionOutcome status = await context.Processor.ProcessAsync(
            Request(
                BrokerCommand.GetStatus,
                new GetStatusRequest(GetStatusQueryType.ByTaskId, taskId)),
            BrokerExecutionContext.OrdinaryUi);
        GetStatusResult statusResult = Assert.IsType<GetStatusResult>(status.Response.Result);
        Assert.Single(statusResult.Tasks);
        Assert.Equal(LockTaskStatus.Active, statusResult.Tasks[0].Status);

        BrokerRequestEnvelope removeRequest = Request(
            BrokerCommand.RemoveLock,
            new RemoveLockRequest(taskId, createResult.RecoveryRecordId));
        BrokerExecutionOutcome unauthorized = await context.Processor.ProcessAsync(
            removeRequest,
            BrokerExecutionContext.OrdinaryUi);
        BrokerExecutionOutcome removed = await context.Processor.ProcessAsync(
            removeRequest with { RequestId = Guid.NewGuid() },
            BrokerExecutionContext.ConsentBrokerInternalScheduler);

        Assert.Equal(BrokerErrorCodes.FSL_E_UNAUTHORIZED_CALLER, unauthorized.Response.Error!.Code);
        Assert.Equal(BrokerExecutionEffect.Succeeded, removed.Effect);
        Assert.True(Assert.IsType<RemoveLockResult>(removed.Response.Result).RecoveryRecordDeleted);
        Assert.Empty(Directory.EnumerateFiles(context.RecordsDirectory));
    }

    [Fact]
    public async Task ValidatePath_ReturnsSixteenDigitVolumeAndCanonicalFileIdHalves()
    {
        using ProcessorContext context = ProcessorContext.Create();
        string target = Path.Combine(context.RecordsDirectory, "target");
        Directory.CreateDirectory(target);

        BrokerExecutionOutcome outcome = await context.Processor.ProcessAsync(
            Request(BrokerCommand.ValidatePath, new ValidatePathRequest(target)),
            BrokerExecutionContext.OrdinaryUi);

        ValidatePathResult result = Assert.IsType<ValidatePathResult>(outcome.Response.Result);
        Assert.Matches("^[0-9a-f]{16}$", result.VolumeSerialNumber);
        Assert.Matches("^(0|[1-9][0-9]*)$", result.FileIdHigh);
        Assert.Matches("^(0|[1-9][0-9]*)$", result.FileIdLow);
    }

    [Fact]
    public async Task CreateLock_ReadinessGateRunsBeforeDomainRegistryRecoveryAndAclWork()
    {
        using ProcessorContext context = ProcessorContext.Create(RecoveryReadinessTests.BlockedGate());
        var request = Request(
            BrokerCommand.CreateLock,
            new CreateLockRequest(Guid.NewGuid(), "not-a-valid-absolute-path", -1));

        BrokerExecutionOutcome outcome = await context.Processor.ProcessAsync(
            request,
            BrokerExecutionContext.OrdinaryUi);

        Assert.Equal(BrokerExecutionEffect.FailedWithoutSideEffects, outcome.Effect);
        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_BLOCKING, outcome.Response.Error!.Code);
        Assert.Equal("Folder restrictions cannot be created until recovery is complete.", outcome.Response.Error.Message);
        Assert.True(outcome.Response.Error.Retryable);
        Assert.Null(outcome.Response.Error.Field);
        Assert.Equal(0, context.FolderLockService.CreateCount);
        Assert.Empty(Directory.EnumerateFiles(context.RecordsDirectory));
    }

    private static BrokerRequestEnvelope Request(BrokerCommand command, IBrokerRequestPayload payload) => new(
        1,
        Guid.NewGuid(),
        command,
        1,
        new DateTimeOffset(2026, 7, 19, 16, 30, 0, TimeSpan.Zero),
        payload);

    private sealed class ProcessorContext : IDisposable
    {
        private ProcessorContext(
            string recordsDirectory,
            BrokerCommandProcessor processor,
            RegistryFolderLockService folderLockService)
        {
            RecordsDirectory = recordsDirectory;
            Processor = processor;
            FolderLockService = folderLockService;
        }

        internal string RecordsDirectory { get; }

        internal BrokerCommandProcessor Processor { get; }

        internal RegistryFolderLockService FolderLockService { get; }

        internal static ProcessorContext Create(RecoveryCreateLockGate? createLockGate = null)
        {
            string recordsDirectory = Path.Combine(
                Path.GetTempPath(),
                "FolderSessionLock.Tests",
                Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(recordsDirectory);
            var clock = new ProcessorClock(
                new DateTimeOffset(2026, 7, 19, 16, 30, 0, TimeSpan.Zero));
            var registry = new RecoveryTaskRegistry();
            var store = RecoveryTestData.CreateStore(recordsDirectory);
            var folderLockService = new RegistryFolderLockService(registry, store, clock);
            var pathRelation = new WindowsFolderPathRelationService();
            var manager = new LockTaskManager(pathRelation);
            var coordinator = new LockTaskCoordinator(
                manager,
                folderLockService,
                clock,
                NullLogger<LockTaskCoordinator>.Instance);
            var validator = new WindowsFolderPathValidator(new FolderPathSafetyPolicy(
                Path.Combine(recordsDirectory, "repository"),
                Path.Combine(recordsDirectory, "installation"),
                []));
            var processor = new BrokerCommandProcessor(
                validator,
                manager,
                coordinator,
                folderLockService,
                registry,
                clock,
                LockDurationPolicy.Create(TimeSpan.FromMinutes(1), TimeSpan.FromHours(24)).Value,
                createLockGate ?? RecoveryReadinessTests.ReadyGate());
            return new ProcessorContext(recordsDirectory, processor, folderLockService);
        }

        public void Dispose()
        {
            if (Directory.Exists(RecordsDirectory))
            {
                Directory.Delete(RecordsDirectory, recursive: true);
            }
        }
    }

    private sealed class RegistryFolderLockService(
        RecoveryTaskRegistry registry,
        FileRecoveryRecordStore store,
        ProcessorClock clock) : IFolderLockService
    {
        internal int CreateCount { get; private set; }

        public async ValueTask<Result<Guid>> CreateLockAsync(
            FolderLockRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            RecoveryRecord applied = RecoveryTestData.Applied() with
            {
                RecordId = Guid.NewGuid(),
                TaskId = request.TaskId,
                NormalizedPath = Path.GetFullPath(request.FolderPath),
                CreatedUtc = clock.UtcNow,
                ExpiresUtc = clock.UtcNow.Add(request.Duration),
                LastUpdatedUtc = clock.UtcNow.AddTicks(1),
            };
            Assert.True(registry.TryAdd(applied));
            Assert.True((await store.WriteNewAsync(applied, cancellationToken)).IsSuccess);
            return Result<Guid>.Success(request.TaskId);
        }

        public async ValueTask<Result> RemoveLockAsync(
            Guid taskId,
            LockRemovalIntent intent,
            CancellationToken cancellationToken = default)
        {
            RecoveryRecord? record = registry.GetByTaskId(taskId);
            if (record is null)
            {
                return Result.Success();
            }

            Result delete = await store.DeleteAsync(record, cancellationToken);
            if (delete.IsSuccess)
            {
                registry.Remove(record.RecordId);
            }

            return delete;
        }
    }

    private sealed class ProcessorClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
