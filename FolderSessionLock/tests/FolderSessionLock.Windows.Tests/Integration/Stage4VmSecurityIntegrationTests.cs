using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Windows.Tests.Integration;

public sealed class Stage4VmSecurityIntegrationTests
{
    [Fact]
    [Trait("Category", "Stage4Vm")]
    [Trait("Checkpoint", "CP10")]
    public async Task InstalledProtectedPaths_PassTheProductionVerifier()
    {
        Assert.Equal(
            "FSL-STAGE4-VM",
            Environment.MachineName,
            ignoreCase: true);
        string expectedInstall = RequiredEnvironmentVariable("FSL_STAGE4_INSTALL_DIRECTORY");
        string expectedProgramData = RequiredEnvironmentVariable("FSL_STAGE4_PROGRAMDATA_ROOT");
        ProtectedPathSet paths = ProtectedPathSet.CreateProduction();
        Assert.Equal(expectedInstall, paths.InstallDirectory, ignoreCase: true);
        Assert.Equal(
            Path.Combine(expectedProgramData, "Recovery"),
            paths.RecoveryRoot,
            ignoreCase: true);

        var verifier = new WindowsProtectedPathSecurityVerifier(paths);
        foreach (ProtectedPathSecurityCheckRequest request in paths.CreateRequests())
        {
            ProtectedPathSecurityCheckResult result = await verifier.VerifyAsync(
                request,
                CancellationToken.None);

            Assert.True(result.IsTrusted, $"{request.PathKind}: {result.ErrorCode}");
            Assert.Null(result.ErrorCode);
        }
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Missing required Stage4Vm variable: {name}");
        return value;
    }
}
