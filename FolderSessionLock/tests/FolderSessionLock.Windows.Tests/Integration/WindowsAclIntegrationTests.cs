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

public sealed class WindowsAclIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsAcl")]
    public async Task MinimumDenyMatrix_UsesOnlyTemporaryNtfsDirectoryAndRestoresAccess()
    {
        AclTestSafetyGate.EnsureCanWrite();
        using TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        AssertTemporaryGuidRoot(temporary.Path);
        string target = Path.Combine(temporary.Path, "target");
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(outside);
        string existing = CreateFile(target, "existing.txt");
        string deleteFile = CreateFile(target, "delete.txt");
        string deleteDirectory = Path.Combine(target, "delete-directory");
        Directory.CreateDirectory(deleteDirectory);
        string renameSource = CreateFile(target, "rename-source.txt");
        string moveSource = CreateFile(target, "move-source.txt");
        string traversedDirectory = Path.Combine(target, "traversed");
        Directory.CreateDirectory(traversedDirectory);
        string traversedFile = CreateFile(traversedDirectory, "inside.txt");
        string inheritedDirectory = Path.Combine(target, "inherited");
        Directory.CreateDirectory(inheritedDirectory);
        string inheritedFile = CreateFile(inheritedDirectory, "inside.txt");
        string protectedDirectory = Path.Combine(target, "protected");
        Directory.CreateDirectory(protectedDirectory);
        string protectedFile = CreateFile(protectedDirectory, "inside.txt");

        WindowsFolderPathValidator validator = CreateValidator(temporary.Path);
        ProtectDacl(validator, protectedDirectory);
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
        using ValidatedDirectory validatedTarget = Validate(validator, target);
        Assert.Equal(Path.GetFullPath(target), validatedTarget.FinalPath, ignoreCase: true);
        validatedTarget.Dispose();

        AssertBaselineOperations(
            target,
            outside,
            existing,
            renameSource,
            moveSource,
            traversedFile);
        AccessProbeResult beforeWriteOwner = WindowsAclAccessProbe.Probe(
            target,
            WindowsAclAccessProbe.WriteOwner,
            directory: true);
        AccessProbeResult beforeSynchronize = WindowsAclAccessProbe.Probe(
            target,
            WindowsAclAccessProbe.Synchronize,
            directory: true);

        var identityProvider = new WindowsSessionIdentityProvider();
        Result<SessionIdentity> identityResult = await identityProvider.GetCurrentAsync();
        Assert.True(identityResult.IsSuccess, identityResult.Error?.Message);
        var logonSid = new SecurityIdentifier(identityResult.Value.LogonSid);
        var editor = new DirectoryAclEditor();
        var service = new WindowsFolderLockService(
            identityProvider,
            validator,
            new WindowsFolderPathRelationService(),
            editor);
        Guid taskId = Guid.NewGuid();
        bool lockCreated = false;

        try
        {
            Result<Guid> createResult = await service.CreateLockAsync(
                new FolderLockRequest(taskId, target, TimeSpan.FromMinutes(1)));
            Assert.True(createResult.IsSuccess, createResult.Error?.Message);
            lockCreated = true;

            AssertAccessDenied(() => Directory.EnumerateFileSystemEntries(target).ToArray());
            AssertAccessDenied(() => File.ReadAllText(existing));
            AssertAccessDenied(() => File.WriteAllText(Path.Combine(target, "new.txt"), "new"));
            AssertAccessDenied(() => File.AppendAllText(existing, "write"));
            AssertAccessDenied(() => Directory.CreateDirectory(Path.Combine(target, "new-directory")));
            AssertAccessDenied(() => File.Delete(deleteFile));
            Assert.Equal(
                new AccessProbeResult(false, NativeMethods.ErrorAccessDenied),
                WindowsAclAccessProbe.RemoveDirectory(deleteDirectory));
            Assert.Equal(
                new AccessProbeResult(false, NativeMethods.ErrorAccessDenied),
                WindowsAclAccessProbe.MoveFile(
                    renameSource,
                    Path.Combine(target, "renamed.txt")));
            Assert.Equal(
                new AccessProbeResult(false, NativeMethods.ErrorAccessDenied),
                WindowsAclAccessProbe.MoveFile(
                    moveSource,
                    Path.Combine(outside, "moved.txt")));
            AssertAccessDenied(() => File.ReadAllText(traversedFile));
            AssertDeniedProbe(existing, WindowsAclAccessProbe.FileReadEa, directory: false);
            AssertDeniedProbe(existing, WindowsAclAccessProbe.FileWriteEa, directory: false);
            AssertAccessDenied(() => File.GetAttributes(existing));
            AssertAccessDenied(() => File.SetAttributes(existing, FileAttributes.Hidden));
            AssertDeniedProbe(existing, WindowsAclAccessProbe.Delete, directory: false);
            AssertDeniedProbe(target, WindowsAclAccessProbe.FileDeleteChild, directory: true);
            AssertSuccessfulProbe(target, WindowsAclAccessProbe.ReadControl, directory: true);
            AssertSuccessfulProbe(target, WindowsAclAccessProbe.WriteDac, directory: true);
            Assert.Equal(beforeWriteOwner, WindowsAclAccessProbe.Probe(
                target,
                WindowsAclAccessProbe.WriteOwner,
                directory: true));
            Assert.Equal(beforeSynchronize, WindowsAclAccessProbe.Probe(
                target,
                WindowsAclAccessProbe.Synchronize,
                directory: true));

            using SafeFileHandle securityHandle = WindowsAclAccessProbe.Open(
                target,
                WindowsAclAccessProbe.ReadControl,
                directory: true);
            Assert.False(securityHandle.IsInvalid);
            Assert.True(editor.ReadSnapshot(securityHandle).IsSuccess);

            AssertInheritedAce(editor, inheritedDirectoryControl, logonSid, expected: true);
            AssertInheritedAce(editor, inheritedFileControl, logonSid, expected: true);
            AssertAccessDenied(() => File.ReadAllText(inheritedFile));
            AssertInheritedAce(editor, protectedDirectoryControl, logonSid, expected: false);
            AssertInheritedAce(editor, protectedFileControl, logonSid, expected: false);
            Assert.Equal("probe", File.ReadAllText(protectedFile));

            uint recoveryRights = WindowsAclAccessProbe.ReadControl
                | WindowsAclAccessProbe.WriteDac
                | WindowsAclAccessProbe.WriteOwner
                | WindowsAclAccessProbe.Synchronize;
            Assert.Equal(0u, ((uint)FolderDenyAccessMask.Value) & recoveryRights);
        }
        finally
        {
            if (lockCreated)
            {
                Result removeResult = await service.RemoveLockAsync(
                    taskId,
                    LockRemovalIntent.TestCleanup);
                if (removeResult.IsFailure)
                {
                    var failure = new InvalidOperationException(removeResult.Error!.Message);
                    AclTestSafetyGate.Block(failure);
                    throw failure;
                }
            }
        }

        Assert.True(Directory.EnumerateFileSystemEntries(target).Any());
        Assert.Equal("probe", File.ReadAllText(existing));
        File.WriteAllText(existing, "restored");
        Assert.Equal("restored", File.ReadAllText(existing));
        string restoredFile = Path.Combine(target, "restored.txt");
        File.WriteAllText(restoredFile, "restored");
        Directory.CreateDirectory(Path.Combine(target, "restored-directory"));
        File.Delete(restoredFile);
        Directory.Delete(Path.Combine(target, "restored-directory"));
    }

    private static void AssertBaselineOperations(
        string target,
        string outside,
        string existing,
        string renameSource,
        string moveSource,
        string traversedFile)
    {
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(target));
        Assert.Equal("probe", File.ReadAllText(existing));
        File.AppendAllText(existing, "x");
        File.WriteAllText(existing, "probe");
        string created = Path.Combine(target, "baseline-created.txt");
        File.WriteAllText(created, "probe");
        File.Delete(created);
        string createdDirectory = Path.Combine(target, "baseline-created-directory");
        Directory.CreateDirectory(createdDirectory);
        Directory.Delete(createdDirectory);
        string renamed = Path.Combine(target, "baseline-renamed.txt");
        File.Move(renameSource, renamed);
        File.Move(renamed, renameSource);
        string moved = Path.Combine(outside, "baseline-moved.txt");
        File.Move(moveSource, moved);
        File.Move(moved, moveSource);
        Assert.Equal("probe", File.ReadAllText(traversedFile));
        FileAttributes attributes = File.GetAttributes(existing);
        File.SetAttributes(existing, attributes | FileAttributes.Hidden);
        File.SetAttributes(existing, attributes);
        AssertSuccessfulProbe(existing, WindowsAclAccessProbe.FileReadEa, directory: false);
        AssertSuccessfulProbe(existing, WindowsAclAccessProbe.FileWriteEa, directory: false);
        AssertSuccessfulProbe(existing, WindowsAclAccessProbe.Delete, directory: false);
        AssertSuccessfulProbe(target, WindowsAclAccessProbe.FileDeleteChild, directory: true);
        AssertSuccessfulProbe(target, WindowsAclAccessProbe.ReadControl, directory: true);
        AssertSuccessfulProbe(target, WindowsAclAccessProbe.WriteDac, directory: true);
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

    private static void AssertInheritedAce(
        DirectoryAclEditor editor,
        SafeFileHandle handle,
        SecurityIdentifier sid,
        bool expected)
    {
        DirectoryAclSnapshot snapshot = editor.ReadSnapshot(handle).Value;
        bool present = snapshot.AceBinaries.Any(binary =>
        {
            GenericAce ace = GenericAce.CreateFromBinaryForm(binary, 0);
            return ace is CommonAce common
                && common.AceQualifier == AceQualifier.AccessDenied
                && common.SecurityIdentifier == sid
                && common.AccessMask == (int)FolderDenyAccessMask.Value
                && common.IsInherited;
        });
        Assert.Equal(expected, present);
    }

    private static void AssertAccessDenied(Action operation)
    {
        Exception? exception = Record.Exception(operation);
        Assert.NotNull(exception);
        Assert.Equal(NativeMethods.ErrorAccessDenied, exception!.HResult & 0xFFFF);
    }

    private static void AssertDeniedProbe(string path, uint access, bool directory)
    {
        AccessProbeResult result = WindowsAclAccessProbe.Probe(path, access, directory);
        Assert.False(result.Success);
        Assert.Equal(NativeMethods.ErrorAccessDenied, result.WindowsError);
    }

    private static void AssertSuccessfulProbe(string path, uint access, bool directory)
    {
        AccessProbeResult result = WindowsAclAccessProbe.Probe(path, access, directory);
        Assert.True(result.Success, $"Windows error {result.WindowsError} for access 0x{access:X8}.");
    }

    private static ValidatedDirectory Validate(
        WindowsFolderPathValidator validator,
        string path)
    {
        Result<ValidatedDirectory> result = validator.Validate(path);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
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

    private static string CreateFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "probe");
        return path;
    }

    private static void AssertTemporaryGuidRoot(string path)
    {
        string requiredRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests"));
        Assert.Equal(requiredRoot, Directory.GetParent(path)!.FullName, ignoreCase: true);
        Assert.True(Guid.TryParseExact(Path.GetFileName(path), "D", out _));
    }
}
