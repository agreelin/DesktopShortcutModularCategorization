using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using FolderSessionLock.Windows.Tests.Infrastructure;

namespace FolderSessionLock.Windows.Tests.Integration;

public sealed class WindowsAclToctouTests
{
    [Fact]
    [Trait("Category", "WindowsAclToctou")]
    public Task CreateLock_PathReplacedBeforeAclWrite_FailsWithoutChangingEitherAcl() =>
        AssertCreateReplacementFailure(ReplacementPhase.BeforeAclWrite);

    [Fact]
    [Trait("Category", "WindowsAclToctou")]
    public Task CreateLock_PathReplacedAfterAclWrite_RollsBackOriginalHandleAndFails() =>
        AssertCreateReplacementFailure(ReplacementPhase.AfterAclWrite);

    [Fact]
    [Trait("Category", "WindowsAclToctou")]
    public async Task RemoveLock_PathReplacedAfterSuccess_RemovesOnlyOriginalHandleAceAndIsIdempotent()
    {
        AclTestSafetyGate.EnsureCanWrite();
        TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        string temporaryPath = temporary.Path;
        string target = Path.Combine(temporaryPath, "target");
        string moved = Path.Combine(temporaryPath, "moved");
        Directory.CreateDirectory(target);
        WindowsFolderPathValidator validator = CreateValidator(temporaryPath);
        var editor = new DirectoryAclEditor();
        DirectoryAclSnapshot originalBefore = ReadSnapshot(validator, editor, target);
        (WindowsFolderLockService service, SecurityIdentifier logonSid) =
            await CreateService(validator, editor, null);
        Guid taskId = Guid.NewGuid();

        try
        {
            try
            {
                Result<Guid> create = await service.CreateLockAsync(
                    new FolderLockRequest(taskId, target, TimeSpan.FromMinutes(1)));
                Assert.True(create.IsSuccess, create.Error?.Message);
                ActiveFolderLockRecord active = Assert.IsType<ActiveFolderLockRecord>(
                    service.GetActiveRecord(taskId));
                Assert.Equal(1, CountApplicationAces(
                    editor.ReadSnapshot(active.Directory.Handle).Value,
                    logonSid));

                Directory.Move(target, moved);
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "replacement.txt"), "replacement");
                DirectoryAclSnapshot replacementBefore = ReadSnapshot(
                    validator,
                    editor,
                    target);

                Result remove = await service.RemoveLockAsync(
                    taskId,
                    LockRemovalIntent.TestCleanup);
                Result repeated = await service.RemoveLockAsync(
                    taskId,
                    LockRemovalIntent.Recovery);

                Assert.True(remove.IsSuccess, remove.Error?.Message);
                Assert.True(repeated.IsSuccess, repeated.Error?.Message);
                DirectoryAclSnapshot originalAfter = ReadSnapshot(validator, editor, moved);
                DirectoryAclSnapshot replacementAfter = ReadSnapshot(validator, editor, target);
                AssertSnapshotsEqual(originalBefore, originalAfter);
                AssertSnapshotsEqual(replacementBefore, replacementAfter);
                Assert.Equal(0, CountApplicationAces(originalAfter, logonSid));
                Assert.Equal(0, CountApplicationAces(replacementAfter, logonSid));
                Assert.Equal("replacement", File.ReadAllText(
                    Path.Combine(target, "replacement.txt")));
                AssertDirectoryAccess(moved);
            }
            finally
            {
                await CleanupActiveLock(service, taskId);
            }

            temporary.VerifyAccessAndDeletion();
        }
        finally
        {
            DisposeTemporary(temporary);
        }

        Assert.False(Directory.Exists(temporaryPath));
    }

    private static async Task AssertCreateReplacementFailure(ReplacementPhase phase)
    {
        AclTestSafetyGate.EnsureCanWrite();
        TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        string temporaryPath = temporary.Path;
        string target = Path.Combine(temporaryPath, "target");
        string moved = Path.Combine(temporaryPath, "moved");
        Directory.CreateDirectory(target);
        WindowsFolderPathValidator validator = CreateValidator(temporaryPath);
        var editor = new DirectoryAclEditor();
        DirectoryAclSnapshot originalBefore = ReadSnapshot(validator, editor, target);
        var hook = new ReplacePathHook(phase, target, moved, validator, editor);
        (WindowsFolderLockService service, SecurityIdentifier logonSid) =
            await CreateService(validator, editor, hook);
        Guid taskId = Guid.NewGuid();

        try
        {
            try
            {
                Result<Guid> create = await service.CreateLockAsync(
                    new FolderLockRequest(taskId, target, TimeSpan.FromMinutes(1)));

                Assert.True(create.IsFailure);
                Assert.Equal("windows.path.mapping_changed", create.Error!.Code);
                Assert.Null(service.GetActiveRecord(taskId));
                Assert.True(hook.Replaced);
                DirectoryAclSnapshot originalAfter = ReadSnapshot(validator, editor, moved);
                DirectoryAclSnapshot replacementAfter = ReadSnapshot(validator, editor, target);
                AssertSnapshotsEqual(originalBefore, originalAfter);
                AssertSnapshotsEqual(hook.ReplacementSnapshot!, replacementAfter);
                Assert.Equal(0, CountApplicationAces(originalAfter, logonSid));
                Assert.Equal(0, CountApplicationAces(replacementAfter, logonSid));
                Assert.Equal("replacement", File.ReadAllText(
                    Path.Combine(target, "replacement.txt")));
            }
            finally
            {
                await CleanupActiveLock(service, taskId);
            }

            temporary.VerifyAccessAndDeletion();
        }
        finally
        {
            DisposeTemporary(temporary);
        }

        Assert.False(Directory.Exists(temporaryPath));
    }

    private static async Task<(WindowsFolderLockService Service, SecurityIdentifier LogonSid)>
        CreateService(
            WindowsFolderPathValidator validator,
            DirectoryAclEditor editor,
            IWindowsFolderLockServiceTestHook? hook)
    {
        var identityProvider = new WindowsSessionIdentityProvider();
        Result<SessionIdentity> identity = await identityProvider.GetCurrentAsync();
        Assert.True(identity.IsSuccess, identity.Error?.Message);
        WindowsFolderLockService service = hook is null
            ? new WindowsFolderLockService(
                identityProvider,
                validator,
                new WindowsFolderPathRelationService(),
                editor)
            : new WindowsFolderLockService(
                identityProvider,
                validator,
                new WindowsFolderPathRelationService(),
                editor,
                hook);
        return (service, new SecurityIdentifier(identity.Value.LogonSid));
    }

    private static async Task CleanupActiveLock(
        WindowsFolderLockService service,
        Guid taskId)
    {
        if (service.GetActiveRecord(taskId) is null)
        {
            return;
        }

        Result cleanup = await service.RemoveLockAsync(taskId, LockRemovalIntent.TestCleanup);
        if (cleanup.IsFailure)
        {
            var failure = new InvalidOperationException(cleanup.Error!.Message);
            AclTestSafetyGate.Block(failure);
            throw failure;
        }
    }

    private static DirectoryAclSnapshot ReadSnapshot(
        WindowsFolderPathValidator validator,
        DirectoryAclEditor editor,
        string path)
    {
        Result<ValidatedDirectory> validation = validator.Validate(path);
        Assert.True(validation.IsSuccess, validation.Error?.Message);
        using ValidatedDirectory directory = validation.Value;
        Result<DirectoryAclSnapshot> snapshot = editor.ReadSnapshot(directory.Handle);
        Assert.True(snapshot.IsSuccess, snapshot.Error?.Message);
        return snapshot.Value;
    }

    private static int CountApplicationAces(
        DirectoryAclSnapshot snapshot,
        SecurityIdentifier logonSid) =>
        snapshot.AceBinaries.Count(binary =>
        {
            GenericAce ace = GenericAce.CreateFromBinaryForm(binary, 0);
            return ace is CommonAce common
                && common.AceQualifier == AceQualifier.AccessDenied
                && common.SecurityIdentifier == logonSid
                && common.AccessMask == (int)FolderDenyAccessMask.Value;
        });

    private static void AssertSnapshotsEqual(
        DirectoryAclSnapshot expected,
        DirectoryAclSnapshot actual) =>
        Assert.True(DirectoryAclEditor.SnapshotsEqual(expected, actual));

    private static void AssertDirectoryAccess(string path)
    {
        string probe = Path.Combine(path, "restored.txt");
        File.WriteAllText(probe, "restored");
        Assert.Equal("restored", File.ReadAllText(probe));
        Assert.Contains(probe, Directory.EnumerateFileSystemEntries(path));
        File.Delete(probe);
    }

    private static WindowsFolderPathValidator CreateValidator(string temporaryRoot)
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
        return new WindowsFolderPathValidator(new FolderPathSafetyPolicy(
            Path.Combine(policyRoot, "Repository"),
            Path.Combine(policyRoot, "Installation"),
            [Path.Combine(policyRoot, "Synchronization")],
            roots));
    }

    private static void DisposeTemporary(TemporaryTestDirectory temporary)
    {
        try
        {
            temporary.Dispose();
        }
        catch (Exception exception)
        {
            AclTestSafetyGate.Block(exception);
            throw;
        }
    }

    private enum ReplacementPhase
    {
        BeforeAclWrite,
        AfterAclWrite,
    }

    private sealed class ReplacePathHook : IWindowsFolderLockServiceTestHook
    {
        private readonly ReplacementPhase _phase;
        private readonly string _target;
        private readonly string _moved;
        private readonly WindowsFolderPathValidator _validator;
        private readonly DirectoryAclEditor _editor;

        internal ReplacePathHook(
            ReplacementPhase phase,
            string target,
            string moved,
            WindowsFolderPathValidator validator,
            DirectoryAclEditor editor)
        {
            _phase = phase;
            _target = target;
            _moved = moved;
            _validator = validator;
            _editor = editor;
        }

        internal bool Replaced { get; private set; }

        internal DirectoryAclSnapshot? ReplacementSnapshot { get; private set; }

        public void BeforeAclWrite()
        {
            if (_phase == ReplacementPhase.BeforeAclWrite)
            {
                Replace();
            }
        }

        public void AfterAclWrite()
        {
            if (_phase == ReplacementPhase.AfterAclWrite)
            {
                Replace();
            }
        }

        private void Replace()
        {
            Directory.Move(_target, _moved);
            Directory.CreateDirectory(_target);
            File.WriteAllText(Path.Combine(_target, "replacement.txt"), "replacement");
            ReplacementSnapshot = ReadSnapshot(_validator, _editor, _target);
            Replaced = true;
        }
    }
}
