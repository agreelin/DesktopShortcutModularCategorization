using System.Reflection;
using FolderSessionLock.App.BrokerClient;
using FolderSessionLock.Broker.Logging;

namespace FolderSessionLock.App.Tests.Stage4;

public sealed class Stage4VmBrokerIntegrationTests
{
    [Fact]
    [Trait("Category", "Stage4Vm")]
    [Trait("Checkpoint", "CP10")]
    public void InstalledBroker_UsesExplicitUnsignedLocalScope()
    {
        Assert.Equal(
            "FSL-STAGE4-VM",
            Environment.MachineName,
            ignoreCase: true);
        string brokerPath = RequiredEnvironmentVariable("FSL_STAGE4_BROKER_PATH");
        string? publisher = WindowsBrokerAuthenticodeVerifier.ReadPublisherThumbprint();
        Assert.Equal(string.Empty, publisher);
        var verifier = new WindowsBrokerAuthenticodeVerifier(
            publisher,
            new WindowsBrokerAuthenticodePlatform());

        FolderSessionLock.Core.Results.Result result = verifier.Verify(brokerPath);

        Assert.True(result.IsSuccess, result.Error?.Code);
    }

    [Theory]
    [InlineData("FolderSessionLock.Broker.exe")]
    [InlineData("FolderSessionLock.Broker.dll")]
    [InlineData("FolderSessionLock.Core.dll")]
    [InlineData("FolderSessionLock.Windows.dll")]
    [Trait("Category", "Stage4Vm")]
    [Trait("Checkpoint", "CP10")]
    public void InstalledBrokerTrustSet_IsNotAcceptedAsAuthenticodeSigned(
        string fileName)
    {
        Assert.Equal(
            "FSL-STAGE4-VM",
            Environment.MachineName,
            ignoreCase: true);
        string brokerPath = RequiredEnvironmentVariable("FSL_STAGE4_BROKER_PATH");
        string path = Path.Combine(Path.GetDirectoryName(brokerPath)!, fileName);

        FolderSessionLock.Core.Results.Result<string> result =
            new WindowsBrokerAuthenticodePlatform().VerifyAndGetSignerThumbprint(path);

        Assert.True(result.IsFailure);
        Assert.Equal(
            FolderSessionLock.Protocol.BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED,
            result.Error!.Code);
    }

    [Fact]
    [Trait("Category", "Stage4Vm")]
    [Trait("Checkpoint", "CP10")]
    public void ProductionComposition_ContainsNoBypassForThePublisherPin()
    {
        ElevationBrokerClient client = ElevationBrokerClient.CreateProduction();
        FieldInfo field = typeof(ElevationBrokerClient).GetField(
            "_authenticode",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.IsType<WindowsBrokerAuthenticodeVerifier>(field.GetValue(client));
    }

    [Fact]
    [Trait("Category", "Stage4Vm")]
    [Trait("Checkpoint", "CP10")]
    public async Task InstalledSecurityDescriptors_AreAcceptedByProductionReaders()
    {
        Assert.Equal(
            "FSL-STAGE4-VM",
            Environment.MachineName,
            ignoreCase: true);

        FolderSessionLock.Core.Results.Result<Microsoft.Extensions.Logging.ILoggerFactory>
            logger = new WindowsProtectedLoggerFactory().Create(
                ProtectedLoggerMode.RecoveryOnce,
                Guid.NewGuid());
        Assert.True(logger.IsSuccess, logger.Error?.Code);
        logger.Value.Dispose();

        var readiness = new WindowsRecoveryReadinessReader();
        _ = await readiness.ReadAsync(CancellationToken.None);
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Missing required Stage4Vm variable: {name}");
        return value;
    }

}
