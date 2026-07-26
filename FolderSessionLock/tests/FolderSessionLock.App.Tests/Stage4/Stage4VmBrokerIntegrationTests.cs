using System.Reflection;
using FolderSessionLock.App.BrokerClient;
using FolderSessionLock.Broker.Logging;

namespace FolderSessionLock.App.Tests.Stage4;

public sealed class Stage4VmBrokerIntegrationTests
{
    [Fact]
    [Trait("Category", "Stage4Vm")]
    [Trait("Checkpoint", "CP10")]
    public void InstalledBroker_HasValidPinnedAuthenticodeSignature()
    {
        Assert.Equal(
            "FSL-STAGE4-VM",
            Environment.MachineName,
            ignoreCase: true);
        string brokerPath = RequiredEnvironmentVariable("FSL_STAGE4_BROKER_PATH");
        string publisher = RequiredEnvironmentVariable("FSL_STAGE4_PUBLISHER_THUMBPRINT");
        var verifier = new WindowsBrokerAuthenticodeVerifier(
            publisher,
            new WindowsBrokerAuthenticodePlatform());

        FolderSessionLock.Core.Results.Result result = verifier.Verify(brokerPath);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(
            publisher.ToUpperInvariant(),
            WindowsBrokerAuthenticodeVerifier.ReadPublisherThumbprint());
    }

    [Theory]
    [InlineData("FolderSessionLock.Broker.exe")]
    [InlineData("FolderSessionLock.Broker.dll")]
    [InlineData("FolderSessionLock.Core.dll")]
    [InlineData("FolderSessionLock.Windows.dll")]
    [Trait("Category", "Stage4Vm")]
    [Trait("Checkpoint", "CP10")]
    public void TamperedBrokerTrustSetFile_IsRejectedByTheProductionAuthenticodeVerifier(
        string fileName)
    {
        Assert.Equal(
            "FSL-STAGE4-VM",
            Environment.MachineName,
            ignoreCase: true);
        string brokerPath = RequiredEnvironmentVariable("FSL_STAGE4_BROKER_PATH");
        string publisher = RequiredEnvironmentVariable("FSL_STAGE4_PUBLISHER_THUMBPRINT");
        string root = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        try
        {
            string copiedBroker = CopyBrokerTrustSet(brokerPath, root);
            var verifier = new WindowsBrokerAuthenticodeVerifier(
                publisher,
                new WindowsBrokerAuthenticodePlatform());
            FolderSessionLock.Core.Results.Result baseline =
                verifier.Verify(copiedBroker);
            Assert.True(baseline.IsSuccess, baseline.Error?.Code);

            string tampered = Path.Combine(root, fileName);
            using (FileStream stream = new(tampered, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.True(stream.Length > 0);
                stream.Position = stream.Length - 1;
                int original = stream.ReadByte();
                stream.Position = stream.Length - 1;
                stream.WriteByte((byte)(original ^ 0x01));
                stream.Flush(flushToDisk: true);
            }

            FolderSessionLock.Core.Results.Result result = verifier.Verify(copiedBroker);

            Assert.True(result.IsFailure);
            Assert.Equal(
                FolderSessionLock.Protocol.BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED,
                result.Error!.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("FolderSessionLock.Broker.exe")]
    [InlineData("FolderSessionLock.Broker.dll")]
    [InlineData("FolderSessionLock.Core.dll")]
    [InlineData("FolderSessionLock.Windows.dll")]
    [Trait("Category", "Stage4Vm")]
    [Trait("Checkpoint", "CP10")]
    public void UnsignedBrokerTrustSetFile_IsRejectedByTheProductionAuthenticodeVerifier(
        string fileName)
    {
        Assert.Equal(
            "FSL-STAGE4-VM",
            Environment.MachineName,
            ignoreCase: true);
        string brokerPath = RequiredEnvironmentVariable("FSL_STAGE4_BROKER_PATH");
        string publisher = RequiredEnvironmentVariable("FSL_STAGE4_PUBLISHER_THUMBPRINT");
        string root = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        try
        {
            string copiedBroker = CopyBrokerTrustSet(brokerPath, root);
            var verifier = new WindowsBrokerAuthenticodeVerifier(
                publisher,
                new WindowsBrokerAuthenticodePlatform());
            FolderSessionLock.Core.Results.Result baseline =
                verifier.Verify(copiedBroker);
            Assert.True(baseline.IsSuccess, baseline.Error?.Code);

            File.WriteAllBytes(Path.Combine(root, fileName), [0x4D, 0x5A, 0x00, 0x00]);

            FolderSessionLock.Core.Results.Result result = verifier.Verify(copiedBroker);

            Assert.True(result.IsFailure);
            Assert.Equal(
                FolderSessionLock.Protocol.BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED,
                result.Error!.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static string CopyBrokerTrustSet(string brokerPath, string destination)
    {
        string source = Path.GetDirectoryName(brokerPath)!;
        foreach (string fileName in new[]
        {
            "FolderSessionLock.Broker.exe",
            "FolderSessionLock.Broker.dll",
            "FolderSessionLock.Core.dll",
            "FolderSessionLock.Windows.dll",
        })
        {
            File.Copy(
                Path.Combine(source, fileName),
                Path.Combine(destination, fileName));
        }

        return Path.Combine(destination, "FolderSessionLock.Broker.exe");
    }
}
