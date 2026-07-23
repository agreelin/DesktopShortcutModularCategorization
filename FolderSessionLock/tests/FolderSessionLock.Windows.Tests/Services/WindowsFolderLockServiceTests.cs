using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using FolderSessionLock.Windows.Tests.Infrastructure;

namespace FolderSessionLock.Windows.Tests.Services;

public sealed class WindowsFolderLockServiceTests
{
    [Fact]
    public void PublicConstructors_DoNotExposeTestHook()
    {
        Type[][] parameterTypes = typeof(WindowsFolderLockService)
            .GetConstructors()
            .Select(constructor => constructor.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray())
            .ToArray();

        Assert.Single(parameterTypes);
        Assert.DoesNotContain(
            parameterTypes[0],
            parameterType => parameterType == typeof(IWindowsFolderLockServiceTestHook));
    }

    [Fact]
    public async Task CreateAndRemove_SucceedsAndRepeatedRemoveIsIdempotent()
    {
        using var context = await LockTestContext.Create();
        Guid taskId = Guid.NewGuid();

        Result<Guid> create = await context.Create(taskId, context.TargetPath);
        Result remove = await context.Service.RemoveLockAsync(taskId, LockRemovalIntent.TestCleanup);
        Result repeated = await context.Service.RemoveLockAsync(taskId, LockRemovalIntent.Recovery);

        Assert.True(create.IsSuccess, create.Error?.Message);
        Assert.True(remove.IsSuccess, remove.Error?.Message);
        Assert.True(repeated.IsSuccess, repeated.Error?.Message);
        Assert.Equal(0, context.CountTargetAces());
    }

    [Fact]
    public async Task CreateAndRemove_CommitsRecoveryStatesAroundAclWrites()
    {
        var recovery = new RecordingRecoveryTransaction();
        using var context = await LockTestContext.Create(recoveryTransaction: recovery);
        Guid taskId = Guid.NewGuid();

        Result<Guid> create = await context.Create(taskId, context.TargetPath);
        Result remove = await context.Service.RemoveLockAsync(taskId, LockRemovalIntent.Expiration);

        Assert.True(create.IsSuccess, create.Error?.Message);
        Assert.True(remove.IsSuccess, remove.Error?.Message);
        Assert.Equal(["Prepared", "Applied", "CleanupPending", "Deleted"], recovery.Events);
        Assert.True(recovery.PreparedBeforeAclWrite);
        Assert.True(recovery.AppliedEvidenceWasFromPostWriteRead);
    }

    [Fact]
    public async Task CreateWithoutLifecycleStop_SimulatesCrashKillOrPowerLossAndRetainsResponsibility()
    {
        var recovery = new RecordingRecoveryTransaction();
        using var context = await LockTestContext.Create(recoveryTransaction: recovery);
        Guid taskId = Guid.NewGuid();

        Result<Guid> create = await context.Create(taskId, context.TargetPath);

        Assert.True(create.IsSuccess, create.Error?.Message);
        Assert.Equal(["Prepared", "Applied"], recovery.Events);
        Assert.Equal(1, context.CountTargetAces());
        Assert.NotNull(context.Service.GetActiveRecord(taskId));
    }

    [Fact]
    public async Task Create_MarkAppliedRollbackThenDeleteFailureReturnsDeleteError()
    {
        var recovery = new MarkAppliedAndDeleteFailingRecoveryTransaction();
        using var context = await LockTestContext.Create(recoveryTransaction: recovery);

        Result<Guid> result = await context.Create(Guid.NewGuid(), context.TargetPath);

        Assert.True(result.IsFailure);
        Assert.Equal("FSL_E_RECOVERY_RECORD_DELETE_FAILED", result.Error!.Code);
        Assert.Equal(ErrorCategory.UnrecoverableError, result.Error.Category);
        Assert.Equal(1, recovery.DeleteCalls);
        Assert.Equal(0, context.CountTargetAces());
    }

    [Fact]
    public async Task RepeatedCreateForSameActiveTask_ReturnsSameIdAndKeepsOneAce()
    {
        using var context = await LockTestContext.Create();
        Guid taskId = Guid.NewGuid();

        Result<Guid> first = await context.Create(taskId, context.TargetPath);
        Result<Guid> second = await context.Create(taskId, context.TargetPath);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(taskId, second.Value);
        Assert.Equal(1, context.CountTargetAces());
    }

    [Fact]
    public async Task RepeatedCreateForSameActiveTask_DifferentPathIsRejected()
    {
        using var context = await LockTestContext.Create();
        Guid taskId = Guid.NewGuid();
        Assert.True((await context.Create(taskId, context.TargetPath)).IsSuccess);

        Result<Guid> repeated = await context.Create(taskId, context.OtherPath);

        AssertTaskIdConflict(repeated);
        Assert.Equal(1, context.CountTargetAces());
        Assert.Equal(0, context.CountAces(context.OtherPath));
    }

    [Fact]
    public async Task RepeatedCreateForSameActiveTask_DifferentDurationIsRejected()
    {
        using var context = await LockTestContext.Create();
        Guid taskId = Guid.NewGuid();
        Assert.True((await context.Create(taskId, context.TargetPath)).IsSuccess);

        Result<Guid> repeated = await context.Create(
            taskId,
            context.TargetPath,
            TimeSpan.FromMinutes(2));

        AssertTaskIdConflict(repeated);
        Assert.Equal(1, context.CountTargetAces());
    }

    [Fact]
    public async Task RepeatedCreateForSameActiveTask_DifferentAccountSidIsRejected()
    {
        using var context = await LockTestContext.Create();
        Guid taskId = Guid.NewGuid();
        Assert.True((await context.Create(taskId, context.TargetPath)).IsSuccess);
        context.IdentityProvider.Current = context.Identity with
        {
            AccountSid = CreateDifferentValidSid(context.Identity.AccountSid),
        };

        Result<Guid> repeated = await context.Create(taskId, context.TargetPath);

        AssertTaskIdConflict(repeated);
        Assert.Equal(1, context.CountTargetAces());
    }

    [Fact]
    public async Task RepeatedCreateForSameActiveTask_DifferentLogonSidIsRejected()
    {
        using var context = await LockTestContext.Create();
        Guid taskId = Guid.NewGuid();
        Assert.True((await context.Create(taskId, context.TargetPath)).IsSuccess);
        context.IdentityProvider.Current = context.Identity with
        {
            LogonSid = CreateDifferentValidSid(context.Identity.LogonSid),
        };

        Result<Guid> repeated = await context.Create(taskId, context.TargetPath);

        AssertTaskIdConflict(repeated);
        Assert.Equal(1, context.CountTargetAces());
    }

    [Fact]
    public async Task RepeatedCreateForSameActiveTask_DifferentWindowsSessionIdIsRejected()
    {
        using var context = await LockTestContext.Create();
        Guid taskId = Guid.NewGuid();
        Assert.True((await context.Create(taskId, context.TargetPath)).IsSuccess);
        context.IdentityProvider.Current = context.Identity with
        {
            WindowsSessionId = checked(context.Identity.WindowsSessionId + 1),
        };

        Result<Guid> repeated = await context.Create(taskId, context.TargetPath);

        AssertTaskIdConflict(repeated);
        Assert.Equal(1, context.CountTargetAces());
    }

    [Fact]
    public async Task RemoveUnknownTaskId_FailsWithoutPathGuessing()
    {
        using var context = await LockTestContext.Create();

        Result result = await context.Service.RemoveLockAsync(
            Guid.NewGuid(),
            LockRemovalIntent.AdministrativeCleanup);

        Assert.True(result.IsFailure);
        Assert.Equal("windows.lock.task_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task DifferentTaskIdForSamePath_IsRejected()
    {
        using var context = await LockTestContext.Create();
        Result<Guid> first = await context.Create(Guid.NewGuid(), context.TargetPath);

        Result<Guid> second = await context.Create(Guid.NewGuid(), context.TargetPath);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsFailure);
        Assert.Equal("windows.lock.path_conflict", second.Error!.Code);
        Assert.Equal(1, context.CountTargetAces());
    }

    [Fact]
    public async Task ParentChildConflict_IsRejectedBeforeOpeningDeniedChild()
    {
        using var context = await LockTestContext.Create(createChild: true);
        Result<Guid> parent = await context.Create(Guid.NewGuid(), context.TargetPath);

        Result<Guid> child = await context.Create(Guid.NewGuid(), context.ChildPath!);

        Assert.True(parent.IsSuccess, parent.Error?.Message);
        Assert.True(child.IsFailure);
        Assert.Equal("windows.lock.path_conflict", child.Error!.Code);
    }

    [Fact]
    public async Task PreCanceledCreate_DoesNotWriteAce()
    {
        using var context = await LockTestContext.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.CreateLockAsync(
                new FolderLockRequest(Guid.NewGuid(), context.TargetPath, TimeSpan.FromMinutes(1)),
                cancellation.Token).AsTask());

        Assert.Equal(0, context.CountTargetAces());
    }

    private static void AssertTaskIdConflict(Result<Guid> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("windows.lock.task_id_conflict", result.Error!.Code);
    }

    private static string CreateDifferentValidSid(string currentSid)
    {
        string localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null).Value;
        if (!string.Equals(currentSid, localSystem, StringComparison.Ordinal))
        {
            return localSystem;
        }

        return new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value;
    }

    private sealed class LockTestContext : IDisposable
    {
        private readonly TemporaryTestDirectory _temporaryDirectory;
        private readonly DirectoryAclEditor _editor;
        private readonly SecurityIdentifier _logonSid;
        private readonly HashSet<Guid> _createdTaskIds = [];

        private LockTestContext(
            TemporaryTestDirectory temporaryDirectory,
            string targetPath,
            string otherPath,
            string? childPath,
            WindowsFolderLockService service,
            DirectoryAclEditor editor,
            SecurityIdentifier logonSid,
            MutableSessionIdentityProvider identityProvider,
            SessionIdentity identity)
        {
            _temporaryDirectory = temporaryDirectory;
            TargetPath = targetPath;
            OtherPath = otherPath;
            ChildPath = childPath;
            Service = service;
            _editor = editor;
            _logonSid = logonSid;
            IdentityProvider = identityProvider;
            Identity = identity;
        }

        internal string TargetPath { get; }

        internal string OtherPath { get; }

        internal string? ChildPath { get; }

        internal WindowsFolderLockService Service { get; }

        internal MutableSessionIdentityProvider IdentityProvider { get; }

        internal SessionIdentity Identity { get; }

        internal static async Task<LockTestContext> Create(
            bool createChild = false,
            IFolderLockRecoveryTransaction? recoveryTransaction = null)
        {
            AclTestSafetyGate.EnsureCanWrite();
            TemporaryTestDirectory temporaryDirectory = TemporaryTestDirectory.Create();
            string targetPath = Path.Combine(temporaryDirectory.Path, "target");
            Directory.CreateDirectory(targetPath);
            string otherPath = Path.Combine(temporaryDirectory.Path, "other");
            Directory.CreateDirectory(otherPath);
            string? childPath = null;
            if (createChild)
            {
                childPath = Path.Combine(targetPath, "child");
                Directory.CreateDirectory(childPath);
            }

            Result<SessionIdentity> identity =
                await new WindowsSessionIdentityProvider().GetCurrentAsync();
            Assert.True(identity.IsSuccess, identity.Error?.Message);
            var sessionProvider = new MutableSessionIdentityProvider(identity.Value);
            var editor = new DirectoryAclEditor();
            var service = new WindowsFolderLockService(
                sessionProvider,
                CreateValidator(temporaryDirectory.Path),
                new WindowsFolderPathRelationService(),
                editor,
                recoveryTransaction ?? InMemoryFolderLockRecoveryTransaction.Instance);
            return new LockTestContext(
                temporaryDirectory,
                targetPath,
                otherPath,
                childPath,
                service,
                editor,
                new SecurityIdentifier(identity.Value.LogonSid),
                sessionProvider,
                identity.Value);
        }

        internal async Task<Result<Guid>> Create(
            Guid taskId,
            string path,
            TimeSpan? duration = null)
        {
            Result<Guid> result = await Service.CreateLockAsync(
                new FolderLockRequest(
                    taskId,
                    path,
                    duration ?? TimeSpan.FromMinutes(1)));
            if (result.IsSuccess)
            {
                _createdTaskIds.Add(taskId);
            }

            return result;
        }

        internal int CountTargetAces()
            => CountAces(TargetPath);

        internal int CountAces(string path)
        {
            ActiveFolderLockRecord? active = _createdTaskIds
                .Select(Service.GetActiveRecord)
                .FirstOrDefault(record => record is not null);
            ValidatedDirectory? opened = null;
            if (active is null
                || !string.Equals(path, TargetPath, StringComparison.OrdinalIgnoreCase))
            {
                opened = Validate(path, _temporaryDirectory.Path);
            }

            Result<DirectoryAclSnapshot> snapshot = _editor.ReadSnapshot(
                opened?.Handle ?? active!.Directory.Handle);
            Assert.True(snapshot.IsSuccess, snapshot.Error?.Message);
            int count = snapshot.Value.AceBinaries.Count(binary =>
            {
                GenericAce ace = GenericAce.CreateFromBinaryForm(binary, 0);
                return ace is CommonAce common
                    && common.AceQualifier == AceQualifier.AccessDenied
                    && common.SecurityIdentifier == _logonSid
                    && common.AccessMask == (int)FolderDenyAccessMask.Value
                    && common.AceFlags == (AceFlags.ContainerInherit | AceFlags.ObjectInherit);
            });
            opened?.Dispose();
            return count;
        }

        public void Dispose()
        {
            try
            {
                foreach (Guid taskId in _createdTaskIds)
                {
                    Result result = Service.RemoveLockAsync(
                        taskId,
                        LockRemovalIntent.TestCleanup).AsTask().GetAwaiter().GetResult();
                    if (result.IsFailure)
                    {
                        throw new InvalidOperationException(result.Error!.Message);
                    }
                }

                _temporaryDirectory.Dispose();
            }
            catch (Exception exception)
            {
                AclTestSafetyGate.Block(exception);
                throw;
            }
        }

        private static WindowsFolderPathValidator CreateValidator(string temporaryRoot) =>
            new(CreatePolicy(temporaryRoot));

        private static ValidatedDirectory Validate(string path, string temporaryRoot)
        {
            Result<ValidatedDirectory> result = CreateValidator(temporaryRoot).Validate(path);
            Assert.True(result.IsSuccess, result.Error?.Message);
            return result.Value;
        }

        private static FolderPathSafetyPolicy CreatePolicy(string temporaryRoot)
        {
            string policyRoot = Path.Combine(temporaryRoot, "Policy");
            var roots = new SystemPathRoots(
                Path.Combine(policyRoot, "User"),
                Path.Combine(policyRoot, "Desktop"),
                Path.Combine(policyRoot, "Documents"),
                Path.Combine(policyRoot, "Downloads"),
                Path.Combine(policyRoot, "Windows"),
                Path.Combine(policyRoot, "System"),
                [Path.Combine(policyRoot, "ProgramFiles")],
                Path.Combine(policyRoot, "ProgramData"));
            return new FolderPathSafetyPolicy(
                Path.Combine(policyRoot, "Repository"),
                Path.Combine(policyRoot, "Installation"),
                [Path.Combine(policyRoot, "Synchronization")],
                roots);
        }
    }

    private sealed class MutableSessionIdentityProvider(SessionIdentity current)
        : ISessionIdentityProvider
    {
        internal SessionIdentity Current { get; set; } = current;

        public ValueTask<Result<SessionIdentity>> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result<SessionIdentity>.Success(Current));
        }
    }

    private sealed class RecordingRecoveryTransaction : IFolderLockRecoveryTransaction
    {
        private readonly Guid _recordId = Guid.NewGuid();

        internal List<string> Events { get; } = [];

        internal bool PreparedBeforeAclWrite { get; private set; }

        internal bool AppliedEvidenceWasFromPostWriteRead { get; private set; }

        public ValueTask<Result<Guid>> PrepareAsync(
            FolderLockRequest request,
            SessionIdentity sessionIdentity,
            ValidatedDirectory directory,
            RecoveryAclEvidence evidence,
            CancellationToken cancellationToken)
        {
            DirectoryAclSnapshot snapshot = new DirectoryAclEditor().ReadSnapshot(directory.Handle).Value;
            PreparedBeforeAclWrite = evidence.PostApplyDaclSha256 is null
                && evidence.BaselineDaclSha256 == RecoveryAclEvidence.ComputeDaclDigest(snapshot);
            Events.Add("Prepared");
            return ValueTask.FromResult(Result<Guid>.Success(_recordId));
        }

        public ValueTask<Result> MarkAppliedAsync(
            Guid recoveryRecordId,
            RecoveryAclEvidence evidence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_recordId, recoveryRecordId);
            AppliedEvidenceWasFromPostWriteRead = evidence.PostApplyDaclSha256 is not null;
            Events.Add("Applied");
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> MarkCleanupPendingAsync(
            Guid recoveryRecordId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_recordId, recoveryRecordId);
            Events.Add("CleanupPending");
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> MarkCleanupFailedAsync(
            Guid recoveryRecordId,
            Error error,
            CancellationToken cancellationToken)
        {
            Events.Add("CleanupFailed");
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> DeleteAsync(
            Guid recoveryRecordId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_recordId, recoveryRecordId);
            Events.Add("Deleted");
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class MarkAppliedAndDeleteFailingRecoveryTransaction
        : IFolderLockRecoveryTransaction
    {
        private readonly Guid _recordId = Guid.NewGuid();

        internal int DeleteCalls { get; private set; }

        public ValueTask<Result<Guid>> PrepareAsync(
            FolderLockRequest request,
            SessionIdentity sessionIdentity,
            ValidatedDirectory directory,
            RecoveryAclEvidence evidence,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<Guid>.Success(_recordId));

        public ValueTask<Result> MarkAppliedAsync(
            Guid recoveryRecordId,
            RecoveryAclEvidence evidence,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Failure(new Error(
                "FSL_E_RECOVERY_RECORD_WRITE_FAILED",
                "FSL_E_RECOVERY_RECORD_WRITE_FAILED",
                ErrorCategory.UnrecoverableError)));

        public ValueTask<Result> MarkCleanupPendingAsync(
            Guid recoveryRecordId,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> MarkCleanupFailedAsync(
            Guid recoveryRecordId,
            Error error,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> DeleteAsync(
            Guid recoveryRecordId,
            CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return ValueTask.FromResult(Result.Failure(new Error(
                "FSL_E_RECOVERY_RECORD_DELETE_FAILED",
                "FSL_E_RECOVERY_RECORD_DELETE_FAILED",
                ErrorCategory.UnrecoverableError)));
        }
    }
}
