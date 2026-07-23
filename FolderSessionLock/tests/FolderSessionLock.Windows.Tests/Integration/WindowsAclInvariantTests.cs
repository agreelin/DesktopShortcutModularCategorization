using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using FolderSessionLock.Windows.Tests.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Tests.Integration;

public sealed class WindowsAclInvariantTests
{
    [Fact]
    [Trait("Category", "WindowsAclInvariant")]
    public async Task LockAndUnlock_PreserveParentOriginalAcesControlAndInheritance()
    {
        AclTestSafetyGate.EnsureCanWrite();
        TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        string temporaryPath = temporary.Path;
        string parent = Path.Combine(temporaryPath, "parent");
        string target = Path.Combine(parent, "target");
        string inheritedDirectory = Path.Combine(target, "inherited");
        string inheritedFile = Path.Combine(inheritedDirectory, "inside.txt");
        string protectedDirectory = Path.Combine(target, "protected");
        string protectedFile = Path.Combine(protectedDirectory, "inside.txt");
        Directory.CreateDirectory(inheritedDirectory);
        Directory.CreateDirectory(protectedDirectory);
        File.WriteAllText(inheritedFile, "inherited");
        File.WriteAllText(protectedFile, "protected");
        WindowsFolderPathValidator validator = CreateValidator(temporaryPath);
        ProtectDacl(validator, protectedDirectory);
        var editor = new DirectoryAclEditor();
        var identityProvider = new WindowsSessionIdentityProvider();
        Result<SessionIdentity> identity = await identityProvider.GetCurrentAsync();
        Assert.True(identity.IsSuccess, identity.Error?.Message);
        var accountSid = new SecurityIdentifier(identity.Value.AccountSid);
        var logonSid = new SecurityIdentifier(identity.Value.LogonSid);
        var service = new WindowsFolderLockService(
            identityProvider,
            validator,
            new WindowsFolderPathRelationService(),
            editor);
        Guid taskId = Guid.NewGuid();

        try
        {
            using ValidatedDirectory parentControl = Validate(validator, parent);
            using ValidatedDirectory targetControl = Validate(validator, target);
            using SafeFileHandle inheritedDirectoryControl = WindowsAclAccessProbe.Open(
                inheritedDirectory,
                WindowsAclAccessProbe.ReadControl,
                directory: true);
            using SafeFileHandle inheritedFileControl = WindowsAclAccessProbe.Open(
                inheritedFile,
                WindowsAclAccessProbe.ReadControl,
                directory: false);
            using SafeFileHandle protectedDirectoryControl = WindowsAclAccessProbe.Open(
                protectedDirectory,
                WindowsAclAccessProbe.ReadControl,
                directory: true);
            using SafeFileHandle protectedFileControl = WindowsAclAccessProbe.Open(
                protectedFile,
                WindowsAclAccessProbe.ReadControl,
                directory: false);
            Assert.False(inheritedDirectoryControl.IsInvalid);
            Assert.False(inheritedFileControl.IsInvalid);
            Assert.False(protectedDirectoryControl.IsInvalid);
            Assert.False(protectedFileControl.IsInvalid);
            DirectoryAclSnapshot parentBefore = editor.ReadSnapshot(parentControl.Handle).Value;
            DirectoryAclSnapshot targetBefore = editor.ReadSnapshot(targetControl.Handle).Value;
            DirectoryAclSnapshot inheritedDirectoryBefore =
                editor.ReadSnapshot(inheritedDirectoryControl).Value;
            DirectoryAclSnapshot inheritedFileBefore = editor.ReadSnapshot(inheritedFileControl).Value;
            DirectoryAclSnapshot protectedDirectoryBefore =
                editor.ReadSnapshot(protectedDirectoryControl).Value;
            DirectoryAclSnapshot protectedFileBefore = editor.ReadSnapshot(protectedFileControl).Value;

            try
            {
                Result<Guid> create = await service.CreateLockAsync(
                    new FolderLockRequest(taskId, target, TimeSpan.FromMinutes(1)));
                Assert.True(create.IsSuccess, create.Error?.Message);
                ActiveFolderLockRecord active = Assert.IsType<ActiveFolderLockRecord>(
                    service.GetActiveRecord(taskId));
                DirectoryAclOperation operation = Assert.IsType<DirectoryAclOperation>(
                    active.AclOperation);
                DirectoryAclSnapshot parentLocked = editor.ReadSnapshot(parentControl.Handle).Value;
                DirectoryAclSnapshot targetLocked =
                    editor.ReadSnapshot(active.Directory.Handle).Value;
                DirectoryAclSnapshot inheritedDirectoryLocked =
                    editor.ReadSnapshot(inheritedDirectoryControl).Value;
                DirectoryAclSnapshot inheritedFileLocked =
                    editor.ReadSnapshot(inheritedFileControl).Value;
                DirectoryAclSnapshot protectedDirectoryLocked =
                    editor.ReadSnapshot(protectedDirectoryControl).Value;
                DirectoryAclSnapshot protectedFileLocked =
                    editor.ReadSnapshot(protectedFileControl).Value;

                AssertSnapshotsEqual(parentBefore, parentLocked);
                AssertSnapshotsEqual(targetBefore, operation.BeforeSnapshot);
                Assert.True(DirectoryAclEditor.IsSingleAddition(
                    targetBefore,
                    targetLocked,
                    operation.AceBinary));
                Assert.Equal(
                    CountExplicitDenyAces(targetBefore, accountSid),
                    CountExplicitDenyAces(targetLocked, accountSid));
                AssertSingleExplicitApplicationAce(targetLocked, logonSid);
                AssertSingleInheritedApplicationAce(
                    inheritedDirectoryBefore,
                    inheritedDirectoryLocked,
                    logonSid);
                AssertSingleInheritedApplicationAce(
                    inheritedFileBefore,
                    inheritedFileLocked,
                    logonSid);
                AssertSnapshotsEqual(protectedDirectoryBefore, protectedDirectoryLocked);
                AssertSnapshotsEqual(protectedFileBefore, protectedFileLocked);

                Result remove = await service.RemoveLockAsync(
                    taskId,
                    LockRemovalIntent.TestCleanup);
                Assert.True(remove.IsSuccess, remove.Error?.Message);

                AssertSnapshotsEqual(parentBefore, editor.ReadSnapshot(parentControl.Handle).Value);
                AssertSnapshotsEqual(targetBefore, editor.ReadSnapshot(targetControl.Handle).Value);
                AssertSnapshotsEqual(
                    inheritedDirectoryBefore,
                    editor.ReadSnapshot(inheritedDirectoryControl).Value);
                AssertSnapshotsEqual(
                    inheritedFileBefore,
                    editor.ReadSnapshot(inheritedFileControl).Value);
                AssertSnapshotsEqual(
                    protectedDirectoryBefore,
                    editor.ReadSnapshot(protectedDirectoryControl).Value);
                AssertSnapshotsEqual(
                    protectedFileBefore,
                    editor.ReadSnapshot(protectedFileControl).Value);
                AssertDirectoryAccess(target);
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

    private static void AssertSingleExplicitApplicationAce(
        DirectoryAclSnapshot snapshot,
        SecurityIdentifier logonSid)
    {
        CommonAce ace = Assert.Single(snapshot.AceBinaries
            .Select(binary => GenericAce.CreateFromBinaryForm(binary, 0))
            .OfType<CommonAce>()
            .Where(common =>
                common.AceQualifier == AceQualifier.AccessDenied
                && common.SecurityIdentifier == logonSid
                && common.AccessMask == (int)FolderDenyAccessMask.Value));
        Assert.Equal(AceFlags.ContainerInherit | AceFlags.ObjectInherit, ace.AceFlags);
        Assert.False(ace.IsInherited);
    }

    private static void AssertSingleInheritedApplicationAce(
        DirectoryAclSnapshot before,
        DirectoryAclSnapshot after,
        SecurityIdentifier logonSid)
    {
        byte[] inheritedAce = Assert.Single(after.AceBinaries.Where(binary =>
        {
            GenericAce ace = GenericAce.CreateFromBinaryForm(binary, 0);
            return ace is CommonAce common
                && common.AceQualifier == AceQualifier.AccessDenied
                && common.SecurityIdentifier == logonSid
                && common.AccessMask == (int)FolderDenyAccessMask.Value
                && common.IsInherited;
        }));
        Assert.Equal(before.ControlFlags, after.ControlFlags);
        Assert.Equal(before.OwnerSid, after.OwnerSid);
        Assert.Equal(before.GroupSid, after.GroupSid);
        Assert.Equal(before.AceBinaries.Count + 1, after.AceBinaries.Count);
        Assert.Equal(
            before.AceBinaries.Select(Convert.ToHexString),
            after.AceBinaries
                .Where(binary => !binary.AsSpan().SequenceEqual(inheritedAce))
                .Select(Convert.ToHexString));
    }

    private static int CountExplicitDenyAces(
        DirectoryAclSnapshot snapshot,
        SecurityIdentifier sid) =>
        snapshot.AceBinaries.Count(binary =>
        {
            GenericAce ace = GenericAce.CreateFromBinaryForm(binary, 0);
            return ace is CommonAce common
                && common.AceQualifier == AceQualifier.AccessDenied
                && common.SecurityIdentifier == sid
                && !common.IsInherited;
        });

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

    private static void ProtectDacl(
        WindowsFolderPathValidator validator,
        string directoryPath)
    {
        using ValidatedDirectory directory = Validate(validator, directoryPath);
        var editor = new DirectoryAclEditor();
        DirectoryAclSnapshot snapshot = editor.ReadSnapshot(directory.Handle).Value;
        nint dacl = Marshal.AllocHGlobal(snapshot.DaclBinary.Length);
        try
        {
            Marshal.Copy(snapshot.DaclBinary, 0, dacl, snapshot.DaclBinary.Length);
            uint error = NativeMethods.SetSecurityInfo(
                directory.Handle,
                NativeMethods.SeObjectType.FileObject,
                NativeMethods.DaclSecurityInformation
                    | NativeMethods.ProtectedDaclSecurityInformation,
                nint.Zero,
                nint.Zero,
                dacl,
                nint.Zero);
            Assert.Equal(NativeMethods.ErrorSuccess, error);
            Assert.True(editor.ReadSnapshot(directory.Handle).Value.IsProtected);
        }
        finally
        {
            Marshal.FreeHGlobal(dacl);
        }
    }

    private static ValidatedDirectory Validate(
        WindowsFolderPathValidator validator,
        string path)
    {
        Result<ValidatedDirectory> result = validator.Validate(path);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private static void AssertDirectoryAccess(string path)
    {
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(path));
        string file = Path.Combine(path, "restored.txt");
        string directory = Path.Combine(path, "restored-directory");
        File.WriteAllText(file, "created");
        File.WriteAllText(file, "written");
        Assert.Equal("written", File.ReadAllText(file));
        Assert.Contains(file, Directory.EnumerateFileSystemEntries(path));
        Directory.CreateDirectory(directory);
        File.Delete(file);
        Directory.Delete(directory);
    }

    private static void AssertSnapshotsEqual(
        DirectoryAclSnapshot expected,
        DirectoryAclSnapshot actual) =>
        Assert.True(DirectoryAclEditor.SnapshotsEqual(expected, actual));

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
}
