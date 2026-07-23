using System.Security.AccessControl;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Tests.Security;

public sealed class WindowsProtectedPathSecurityVerifierTests
{
    [Fact]
    public async Task VerifyAsync_AcceptsAllFourFixedPathKindsWithTrustedEvidence()
    {
        ProtectedPathSet paths = Paths();
        var platform = new FakePlatform();
        var verifier = new WindowsProtectedPathSecurityVerifier(
            paths,
            platform,
            new ProtectedPathAclPolicy());

        foreach (ProtectedPathSecurityCheckRequest request in paths.CreateRequests())
        {
            platform.Descriptor = request.PathKind == ProtectedPathKind.InstallDirectory
                ? InstallDescriptor()
                : ProtectedPathAclPolicyTests.RecoveryDescriptor();
            ProtectedPathSecurityCheckResult result = await verifier.VerifyAsync(request, default);

            Assert.True(result.IsTrusted);
            Assert.Null(result.ErrorCode);
        }
    }

    [Theory]
    [InlineData("not-found", BrokerErrorCodes.FSL_E_PROTECTED_PATH_NOT_FOUND)]
    [InlineData("open", BrokerErrorCodes.FSL_E_PROTECTED_PATH_OPEN_FAILED)]
    [InlineData("reparse", BrokerErrorCodes.FSL_E_PROTECTED_PATH_REPARSE_POINT)]
    [InlineData("final", BrokerErrorCodes.FSL_E_PROTECTED_PATH_FINAL_PATH_MISMATCH)]
    [InlineData("volume", BrokerErrorCodes.FSL_E_PROTECTED_PATH_VOLUME_UNSUPPORTED)]
    [InlineData("identity", BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_UNAVAILABLE)]
    [InlineData("changed", BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_CHANGED)]
    [InlineData("security", BrokerErrorCodes.FSL_E_PROTECTED_PATH_SECURITY_READ_FAILED)]
    [InlineData("owner", BrokerErrorCodes.FSL_E_PROTECTED_PATH_OWNER_MISMATCH)]
    [InlineData("missing", BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISSING)]
    [InlineData("null", BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_NULL)]
    [InlineData("dacl", BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH)]
    [InlineData("inheritance", BrokerErrorCodes.FSL_E_PROTECTED_PATH_INHERITANCE_INVALID)]
    public async Task VerifyAsync_MapsEachOrderedFailureToTheExactCode(
        string failure,
        string expectedCode)
    {
        ProtectedPathSet paths = Paths();
        var platform = new FakePlatform { Failure = failure };
        var verifier = new WindowsProtectedPathSecurityVerifier(
            paths,
            platform,
            new ProtectedPathAclPolicy());

        ProtectedPathSecurityCheckResult result = await verifier.VerifyAsync(
            new(ProtectedPathKind.RecoveryRecordsDirectory, paths.RecoveryRecordsDirectory),
            default);

        Assert.False(result.IsTrusted);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public async Task VerifyAsync_RejectsAnExpectedPathNotProducedByThePathSet()
    {
        ProtectedPathSet paths = Paths();
        var verifier = new WindowsProtectedPathSecurityVerifier(
            paths,
            new FakePlatform(),
            new ProtectedPathAclPolicy());

        ProtectedPathSecurityCheckResult result = await verifier.VerifyAsync(
            new(ProtectedPathKind.RecoveryRoot, paths.RecoveryRecordsDirectory),
            default);

        Assert.False(result.IsTrusted);
        Assert.Equal(BrokerErrorCodes.FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED, result.ErrorCode);
    }

    private static ProtectedPathSet Paths()
    {
        string root = Path.Combine(Path.GetTempPath(), "FolderSessionLock.Tests", Guid.NewGuid().ToString("D"));
        return ProtectedPathSet.CreateForTest(Path.Combine(root, "ProgramFiles"), Path.Combine(root, "ProgramData"));
    }

    private static ProtectedPathSecurityDescriptor InstallDescriptor() => new(
        ProtectedPathAclPolicy.SystemSid,
        true,
        false,
        ControlFlags.DiscretionaryAclPresent,
        [
            Ace(ProtectedPathAclPolicy.SystemSid, FileSystemRights.FullControl),
            Ace(ProtectedPathAclPolicy.AdministratorsSid, FileSystemRights.FullControl),
            Ace(ProtectedPathAclPolicy.UsersSid, FileSystemRights.ReadAndExecute),
        ]);

    private static ProtectedPathAce Ace(string sid, FileSystemRights rights) => new(
        true,
        sid,
        (int)rights,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        false);

    private sealed class FakePlatform : WindowsProtectedPathSecurityPlatform
    {
        private int _identityReads;

        internal string? Failure { get; init; }

        internal ProtectedPathSecurityDescriptor Descriptor { get; set; } =
            ProtectedPathAclPolicyTests.RecoveryDescriptor();

        internal override bool DirectoryExists(string path) => Failure != "not-found";

        internal override Result<SafeFileHandle> OpenDirectory(string path)
        {
            CurrentPath = path;
            return Failure == "open"
                ? Fail<SafeFileHandle>(BrokerErrorCodes.FSL_E_PROTECTED_PATH_OPEN_FAILED)
                : Result<SafeFileHandle>.Success(new SafeFileHandle(new nint(1), ownsHandle: false));
        }

        internal override Result<NativeMethods.FileAttributeTagInfo> GetAttributes(SafeFileHandle handle) =>
            Result<NativeMethods.FileAttributeTagInfo>.Success(new NativeMethods.FileAttributeTagInfo
            {
                FileAttributes = NativeMethods.FileAttributeDirectory
                    | (Failure == "reparse" ? NativeMethods.FileAttributeReparsePoint : 0),
            });

        internal override Result<string> GetFinalPath(SafeFileHandle handle) =>
            Result<string>.Success(Failure == "final" ? @"C:\Different" : CurrentPath!);

        internal string? CurrentPath { get; set; }

        internal override uint GetDriveType(string rootPath) => Failure == "volume"
            ? NativeMethods.DriveRemote
            : NativeMethods.DriveFixed;

        internal override Result<string> GetFileSystemName(SafeFileHandle handle) =>
            Result<string>.Success("NTFS");

        internal override Result<DirectoryIdentity> GetIdentity(SafeFileHandle handle)
        {
            _identityReads++;
            if (Failure == "identity")
            {
                return Fail<DirectoryIdentity>(BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_UNAVAILABLE);
            }

            return Result<DirectoryIdentity>.Success(
                Failure == "changed" && _identityReads > 1
                    ? new DirectoryIdentity(1, 2, 4)
                    : new DirectoryIdentity(1, 2, 3));
        }

        internal override Result<ProtectedPathSecurityDescriptor> ReadSecurity(SafeFileHandle handle)
        {
            if (Failure == "security")
            {
                return Fail<ProtectedPathSecurityDescriptor>(
                    BrokerErrorCodes.FSL_E_PROTECTED_PATH_SECURITY_READ_FAILED);
            }

            ProtectedPathSecurityDescriptor value = Failure switch
            {
                "owner" => Descriptor with { OwnerSid = ProtectedPathAclPolicy.UsersSid },
                "missing" => Descriptor with { DaclPresent = false },
                "null" => Descriptor with { DaclNull = true },
                "dacl" => Descriptor with { Aces = [] },
                "inheritance" => Descriptor with
                {
                    ControlFlags = ControlFlags.DiscretionaryAclPresent,
                },
                _ => Descriptor,
            };
            return Result<ProtectedPathSecurityDescriptor>.Success(value);
        }

        private static Result<T> Fail<T>(string code) => Result<T>.Failure(new Error(
            code,
            code,
            ErrorCategory.UnrecoverableError));
    }
}
