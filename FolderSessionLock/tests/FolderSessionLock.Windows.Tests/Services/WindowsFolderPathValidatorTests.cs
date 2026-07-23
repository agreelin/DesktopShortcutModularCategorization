using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Services;
using FolderSessionLock.Windows.Tests.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Tests.Services;

public sealed class WindowsFolderPathValidatorTests
{
    [Fact]
    public void Validate_SafeTemporaryNtfsDirectoryReturnsIdentityAndOwnedHandle()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));

        Result<ValidatedDirectory> result = validator.Validate(directory.Path);

        Assert.True(result.IsSuccess, result.Error?.Message);
        SafeFileHandle handle = result.Value.Handle;
        using (result.Value)
        {
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Path)),
                result.Value.NormalizedPath,
                ignoreCase: true);
            Assert.Equal(result.Value.NormalizedPath, result.Value.FinalPath, ignoreCase: true);
            Assert.Equal(32, result.Value.Identity.FileId128.Length);
            Assert.Equal(16, result.Value.Identity.VolumeSerialNumberText.Length);
            Assert.Equal(
                result.Value.Identity.FileId128,
                Convert.ToHexString(result.Value.Identity.GetFileIdBytes()).ToLowerInvariant());
            Assert.False(handle.IsInvalid);
            Assert.False(handle.IsClosed);
            Assert.True(result.Value.HasReadControl);
            Assert.True(result.Value.HasWriteDac);
        }

        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void DirectoryIdentity_UsesTheD022FixedLittleEndianVector()
    {
        byte[] fileId = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");

        DirectoryIdentity identity = DirectoryIdentity.FromFileId(0x0123456789abcdef, fileId);

        Assert.Equal("0123456789abcdef", identity.VolumeSerialNumberText);
        Assert.Equal("1084818905618843912", identity.FileIdHighText);
        Assert.Equal("506097522914230528", identity.FileIdLowText);
        Assert.Equal(fileId, identity.GetFileIdBytes());
    }

    [Fact]
    public void DirectoryIdentity_PreservesUInt64BoundariesAndZeroFileId()
    {
        DirectoryIdentity zero = DirectoryIdentity.FromFileId(0, new byte[16]);
        DirectoryIdentity maximum = DirectoryIdentity.FromFileId(
            ulong.MaxValue,
            Enumerable.Repeat(byte.MaxValue, 16).ToArray());

        Assert.Equal("0000000000000000", zero.VolumeSerialNumberText);
        Assert.Equal("0", zero.FileIdHighText);
        Assert.Equal("0", zero.FileIdLowText);
        Assert.Equal(new byte[16], zero.GetFileIdBytes());
        Assert.Equal("ffffffffffffffff", maximum.VolumeSerialNumberText);
        Assert.Equal(ulong.MaxValue.ToString(), maximum.FileIdHighText);
        Assert.Equal(ulong.MaxValue.ToString(), maximum.FileIdLowText);
        Assert.Equal(Enumerable.Repeat(byte.MaxValue, 16), maximum.GetFileIdBytes());
    }

    [Fact]
    public void DirectoryIdentity_ChangesForEveryFileIdByteAndVolumeSerial()
    {
        byte[] fileId = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        DirectoryIdentity expected = DirectoryIdentity.FromFileId(0x0123456789abcdef, fileId);

        for (int index = 0; index < fileId.Length; index++)
        {
            byte[] changed = fileId.ToArray();
            changed[index] ^= 0xff;

            Assert.NotEqual(expected, DirectoryIdentity.FromFileId(0x0123456789abcdef, changed));
        }

        Assert.NotEqual(expected, DirectoryIdentity.FromFileId(0x0123456789abcdee, fileId));
        Assert.Equal(fileId, new DirectoryIdentity(
            expected.VolumeSerialNumber,
            expected.FileIdHigh,
            expected.FileIdLow).GetFileIdBytes());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankPathIsRejected(string path)
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));

        Result<ValidatedDirectory> result = validator.Validate(path);

        AssertFailure(result, "windows.path.empty");
    }

    [Fact]
    public void Validate_RelativePathIsRejected()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));

        Result<ValidatedDirectory> result = validator.Validate(@"relative\directory");

        AssertFailure(result, "windows.path.relative");
    }

    [Fact]
    public void Validate_UncPathIsRejectedWithoutAccess()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));

        Result<ValidatedDirectory> result = validator.Validate(@"\\server\share\directory");

        AssertFailure(result, "windows.path.unc");
    }

    [Fact]
    public void Validate_VolumeRootIsRejectedWithoutAccess()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));
        string root = Path.GetPathRoot(directory.Path)!;

        Result<ValidatedDirectory> result = validator.Validate(root);

        AssertFailure(result, "windows.path.root");
    }

    [Fact]
    public void Validate_NonexistentPathInsideTemporaryRootIsRejected()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));
        string path = Path.Combine(directory.Path, "missing");

        Result<ValidatedDirectory> result = validator.Validate(path);

        AssertFailure(result, "windows.path.not_found");
    }

    [Fact]
    public void Validate_FileInsideTemporaryRootIsRejected()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));
        string path = Path.Combine(directory.Path, "file.txt");
        File.WriteAllText(path, "probe");

        Result<ValidatedDirectory> result = validator.Validate(path);

        AssertFailure(result, "windows.path.not_directory");
    }

    [Fact]
    public void SafetyPolicy_RejectsExplicitProtectedRootsAndDescendants()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string userProfile = Path.Combine(directory.Path, "UserProfile");
        string desktop = Path.Combine(userProfile, "DesktopExact");
        string documents = Path.Combine(userProfile, "DocumentsExact");
        string downloads = Path.Combine(userProfile, "DownloadsExact");
        string windows = Path.Combine(directory.Path, "WindowsExact");
        string system = Path.Combine(windows, "SystemExact");
        string programFiles = Path.Combine(directory.Path, "ProgramFilesExact");
        string programFilesX86 = Path.Combine(directory.Path, "ProgramFilesX86Exact");
        string programData = Path.Combine(directory.Path, "ProgramDataExact");
        string repository = Path.Combine(directory.Path, "RepositoryExact");
        string installation = Path.Combine(directory.Path, "InstallationExact");
        string synchronization = Path.Combine(directory.Path, "SynchronizationExact");
        var systemRoots = new SystemPathRoots(
            userProfile,
            desktop,
            documents,
            downloads,
            windows,
            system,
            [programFiles, programFilesX86],
            programData);
        var policy = new FolderPathSafetyPolicy(
            repository,
            installation,
            [synchronization],
            systemRoots);
        string[] protectedPaths =
        [
            userProfile,
            desktop,
            Path.Combine(desktop, "Child"),
            documents,
            Path.Combine(documents, "Child"),
            downloads,
            Path.Combine(downloads, "Child"),
            windows,
            Path.Combine(windows, "Child"),
            system,
            programFiles,
            Path.Combine(programFiles, "Child"),
            programFilesX86,
            programData,
            Path.Combine(programData, "Child"),
            repository,
            Path.Combine(repository, "Child"),
            installation,
            Path.Combine(installation, "Child"),
            synchronization,
            Path.Combine(synchronization, "Child"),
        ];

        foreach (string protectedPath in protectedPaths)
        {
            Result result = policy.Validate(protectedPath);
            Assert.True(result.IsFailure, protectedPath);
            Assert.Equal("windows.path.protected", result.Error!.Code);
        }

        Assert.True(policy.Validate(Path.Combine(directory.Path, "SafeSibling")).IsSuccess);
    }

    [Theory]
    [InlineData(NativeMethods.DriveRemovable)]
    [InlineData(NativeMethods.DriveRemote)]
    [InlineData(NativeMethods.DriveCdRom)]
    public void Validate_NonFixedDriveTypeIsRejectedWithoutExternalVolumeAccess(uint driveType)
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var platform = new TestWindowsFolderPathPlatform { DriveType = driveType };
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path), platform);

        Result<ValidatedDirectory> result = validator.Validate(directory.Path);

        AssertFailure(result, "windows.path.drive_not_fixed");
    }

    [Fact]
    public void Validate_NonNtfsNameIsRejectedWithoutExternalVolumeAccess()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var platform = new TestWindowsFolderPathPlatform { FileSystemName = "ReFS" };
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path), platform);

        Result<ValidatedDirectory> result = validator.Validate(directory.Path);

        AssertFailure(result, "windows.path.file_system_not_ntfs");
    }

    [Fact]
    public void Validate_WriteDacAccessFailureIsReturnedWithoutAclWrite()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        var platform = new TestWindowsFolderPathPlatform { DenyValidationAccess = true };
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path), platform);

        Result<ValidatedDirectory> result = validator.Validate(directory.Path);

        AssertFailure(result, "windows.path.insufficient_permissions");
    }

    [Fact]
    public void Validate_TargetReparsePointInsideTemporaryRootIsRejected()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string target = Path.Combine(directory.Path, "Target");
        Directory.CreateDirectory(target);
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));

        string junctionPath;
        Result<ValidatedDirectory> result;
        using (TemporaryDirectoryJunction junction = TemporaryDirectoryJunction.Create(
                   directory,
                   "TargetLink",
                   "Target"))
        {
            junctionPath = junction.Path;
            result = validator.Validate(junction.Path);
        }

        AssertFailure(result, "windows.path.reparse_point");
        Assert.False(Directory.Exists(junctionPath));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void VerifyCurrentPathMapping_UsesZeroAccessIndependentHandles()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string target = Path.Combine(directory.Path, "Target");
        Directory.CreateDirectory(target);
        var platform = new TestWindowsFolderPathPlatform();
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path), platform);
        using ValidatedDirectory validated = validator.Validate(target).Value;
        platform.OpenedAccessMasks.Clear();

        Result result = validator.VerifyCurrentPathMapping(validated);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotEmpty(platform.OpenedAccessMasks);
        Assert.All(platform.OpenedAccessMasks, access => Assert.Equal(0u, access));
    }

    [Fact]
    public void VerifyCurrentPathMapping_RejectsReplacementDirectoryIdentity()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string target = Path.Combine(directory.Path, "Target");
        string moved = Path.Combine(directory.Path, "Moved");
        Directory.CreateDirectory(target);
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));
        using ValidatedDirectory validated = validator.Validate(target).Value;
        Directory.Move(target, moved);
        Directory.CreateDirectory(target);

        Result result = validator.VerifyCurrentPathMapping(validated);

        Assert.True(result.IsFailure);
        Assert.Equal("windows.path.mapping_changed", result.Error!.Code);
    }

    [Fact]
    public void Validate_AncestorReparsePointInsideTemporaryRootIsRejected()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string target = Path.Combine(directory.Path, "Target");
        string child = Path.Combine(target, "Child");
        Directory.CreateDirectory(child);
        var validator = new WindowsFolderPathValidator(CreatePolicy(directory.Path));

        using TemporaryDirectoryJunction junction = TemporaryDirectoryJunction.Create(
            directory,
            "AncestorLink",
            "Target");
        Result<ValidatedDirectory> result = validator.Validate(
            Path.Combine(junction.Path, "Child"));

        AssertFailure(result, "windows.path.reparse_point");
    }

    private static FolderPathSafetyPolicy CreatePolicy(string temporaryRoot)
    {
        string userProfile = Path.Combine(temporaryRoot, "PolicyUserProfile");
        var systemRoots = new SystemPathRoots(
            userProfile,
            Path.Combine(userProfile, "Desktop"),
            Path.Combine(userProfile, "Documents"),
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(temporaryRoot, "PolicyWindows"),
            Path.Combine(temporaryRoot, "PolicyWindows", "System"),
            [Path.Combine(temporaryRoot, "PolicyProgramFiles")],
            Path.Combine(temporaryRoot, "PolicyProgramData"));
        return new FolderPathSafetyPolicy(
            Path.Combine(temporaryRoot, "PolicyRepository"),
            Path.Combine(temporaryRoot, "PolicyInstallation"),
            [Path.Combine(temporaryRoot, "PolicySynchronization")],
            systemRoots);
    }

    private static void AssertFailure(Result<ValidatedDirectory> result, string errorCode)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(errorCode, result.Error!.Code);
    }

    private sealed class TestWindowsFolderPathPlatform : WindowsFolderPathPlatform
    {
        internal List<uint> OpenedAccessMasks { get; } = [];

        internal uint? DriveType { get; init; }

        internal string? FileSystemName { get; init; }

        internal bool DenyValidationAccess { get; init; }

        internal override uint GetDriveType(string rootPath) =>
            DriveType ?? base.GetDriveType(rootPath);

        internal override Result<string> GetFileSystemName(SafeFileHandle handle) =>
            FileSystemName is null
                ? base.GetFileSystemName(handle)
                : Result<string>.Success(FileSystemName);

        internal override Result<SafeFileHandle> OpenPath(string path, uint desiredAccess)
        {
            OpenedAccessMasks.Add(desiredAccess);
            if (DenyValidationAccess && (desiredAccess & NativeMethods.WriteDac) != 0)
            {
                return Result<SafeFileHandle>.Failure(new Error(
                    "windows.path.insufficient_permissions",
                    "The test platform denied WRITE_DAC access.",
                    ErrorCategory.InsufficientPermissions));
            }

            return base.OpenPath(path, desiredAccess);
        }
    }
}
