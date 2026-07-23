using FolderSessionLock.Broker.Security;
using FolderSessionLock.Broker.Transport;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class BrokerStartupOptionsTests
{
    [Theory]
    [InlineData("recovery-service", BrokerRunMode.RecoveryService)]
    [InlineData("recovery-once", BrokerRunMode.RecoveryOnce)]
    public void TryParse_AcceptsOnlyTheExactTwoArgumentRecoveryModes(
        string mode,
        BrokerRunMode expected)
    {
        Assert.True(BrokerStartupOptions.TryParse(["--mode", mode], out BrokerStartupOptions? options));
        Assert.Equal(expected, options!.RunMode);
        Assert.Null(options.ConsentOptions);

        Assert.False(BrokerStartupOptions.TryParse(["--mode", mode, "--path", @"C:\Data"], out _));
        Assert.False(BrokerStartupOptions.TryParse(["--mode", mode.ToUpperInvariant()], out _));
        Assert.False(BrokerStartupOptions.TryParse(["--mode", mode, "--mode", mode], out _));
    }

    [Fact]
    public void TryParse_PreservesTheExactConsentBrokerContract()
    {
        Guid requestId = Guid.Parse("11111111-2222-4333-8444-555555555555");
        string[] arguments =
        [
            "--mode",
            "consent-broker",
            "--pipe-name",
            "FolderSessionLock.Broker.v1",
            "--session-id",
            "1",
            "--request-id",
            requestId.ToString("D"),
            "--client-process-id",
            "1234",
            "--client-process-creation-filetime",
            "133970112000000000",
        ];

        Assert.True(BrokerStartupOptions.TryParse(arguments, out BrokerStartupOptions? options));
        Assert.Equal(BrokerRunMode.ConsentBroker, options!.RunMode);
        Assert.Equal(
            new BrokerConsentOptions(
                BrokerPipeEndpoint.PipeName,
                1,
                requestId,
                1234,
                133970112000000000),
            options.ConsentOptions);
    }

    [Theory]
    [InlineData()]
    [InlineData("--mode")]
    [InlineData("--mode", "RecoveryOnce")]
    [InlineData("--mode", "unknown")]
    [InlineData("--path", "recovery-once")]
    public void TryParse_RejectsEveryOtherShape(params string[] arguments)
    {
        Assert.False(BrokerStartupOptions.TryParse(arguments, out BrokerStartupOptions? options));
        Assert.Null(options);
    }
}
