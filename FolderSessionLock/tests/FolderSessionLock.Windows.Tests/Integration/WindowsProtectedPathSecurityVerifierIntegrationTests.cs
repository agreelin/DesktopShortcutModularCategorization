using System.Security.AccessControl;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Windows.Tests.Integration;

public sealed class WindowsProtectedPathSecurityVerifierIntegrationTests
{
    [Fact]
    public async Task TempDirectory_IsReadOnlyInspectedAndFailsTheProductionOwnerPolicy()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        string programFiles = Path.Combine(root, "ProgramFiles");
        string programData = Path.Combine(root, "ProgramData");
        ProtectedPathSet paths = ProtectedPathSet.CreateForTest(programFiles, programData);
        Directory.CreateDirectory(paths.RecoveryRoot);
        try
        {
            byte[] securityBefore = new DirectoryInfo(paths.RecoveryRoot)
                .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
                .GetSecurityDescriptorBinaryForm();
            var verifier = new WindowsProtectedPathSecurityVerifier(paths);

            ProtectedPathSecurityCheckResult result = await verifier.VerifyAsync(
                new(ProtectedPathKind.RecoveryRoot, paths.RecoveryRoot),
                default);

            Assert.False(result.IsTrusted);
            Assert.Equal(BrokerErrorCodes.FSL_E_PROTECTED_PATH_OWNER_MISMATCH, result.ErrorCode);
            byte[] securityAfter = new DirectoryInfo(paths.RecoveryRoot)
                .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
                .GetSecurityDescriptorBinaryForm();
            Assert.Equal(securityBefore, securityAfter);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
