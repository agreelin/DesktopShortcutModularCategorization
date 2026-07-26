using System.Reflection;
using FolderSessionLock.App.BrokerClient;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.App.Tests.BrokerClient;

public sealed class WindowsBrokerAuthenticodeVerifierTests
{
    private const string Publisher = "00112233445566778899AABBCCDDEEFF00112233";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("001122")]
    [InlineData("00112233445566778899AABBCCDDEEFF0011223Z")]
    [InlineData("00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF 00 11 22 33")]
    public void Verify_FailsClosedForMissingOrMalformedPublisherPin(string? publisher)
    {
        var platform = new AuthenticodePlatform(true, Publisher);
        var verifier = new WindowsBrokerAuthenticodeVerifier(publisher, platform);

        Result result = verifier.Verify(@"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe");

        AssertPathFailure(result);
        Assert.Equal(0, platform.Calls);
    }

    [Fact]
    public void Verify_RequiresValidSignatureBeforeReadingSigner()
    {
        var platform = new AuthenticodePlatform(false, Publisher);
        var verifier = new WindowsBrokerAuthenticodeVerifier(Publisher, platform);

        Result result = verifier.Verify(@"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe");

        AssertPathFailure(result);
        Assert.Equal(1, platform.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00112233445566778899AABBCCDDEEFF00112234")]
    public void Verify_FailsClosedForMissingMalformedOrMismatchedSigner(string? signer)
    {
        var platform = new AuthenticodePlatform(true, signer);
        var verifier = new WindowsBrokerAuthenticodeVerifier(Publisher, platform);

        Result result = verifier.Verify(@"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe");

        AssertPathFailure(result);
        Assert.Equal(1, platform.Calls);
    }

    [Fact]
    public void Verify_AllowsOnlyMatchingPublisherThumbprint()
    {
        var platform = new AuthenticodePlatform(
            true,
            Publisher.ToLowerInvariant());
        var verifier = new WindowsBrokerAuthenticodeVerifier(Publisher, platform);

        Result result = verifier.Verify(@"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe");

        Assert.True(result.IsSuccess);
        Assert.Equal(4, platform.Calls);
        Assert.Equal(
            [
                @"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe",
                @"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.dll",
                @"C:\Program Files\FolderSessionLock\FolderSessionLock.Core.dll",
                @"C:\Program Files\FolderSessionLock\FolderSessionLock.Windows.dll",
            ],
            platform.Paths);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Verify_FailsClosedWhenAManagedBrokerDependencyIsUntrusted(int failOnCall)
    {
        var platform = new AuthenticodePlatform(true, Publisher, failOnCall);
        var verifier = new WindowsBrokerAuthenticodeVerifier(Publisher, platform);

        Result result = verifier.Verify(
            @"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe");

        AssertPathFailure(result);
        Assert.Equal(failOnCall, platform.Calls);
    }

    [Fact]
    public void AppAssembly_ContainsTheNonSecretPublisherPinMetadata()
    {
        AssemblyMetadataAttribute metadata = Assert.Single(
            typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key
                == WindowsBrokerAuthenticodeVerifier.MetadataName);

        Assert.NotNull(metadata.Value);
        Assert.Equal(
            metadata.Value,
            WindowsBrokerAuthenticodeVerifier.ReadPublisherThumbprint());
    }

    [Fact]
    public void Platform_ClosesTheSameTrustSessionAfterReadingSigner()
    {
        var session = new TrustSession(true, Publisher);
        var platform = new WindowsBrokerAuthenticodePlatform(
            new TrustSessionFactory(session));

        Result<string> result = platform.VerifyAndGetSignerThumbprint(
            @"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe");

        Assert.True(result.IsSuccess);
        Assert.Equal(Publisher, result.Value);
        Assert.Equal(1, session.SignerCalls);
        Assert.Equal(1, session.DisposeCalls);
        Assert.True(session.SignerReadBeforeDispose);
    }

    [Fact]
    public void Platform_ClosesFailedTrustSessionWithoutReadingSigner()
    {
        var session = new TrustSession(false, Publisher);
        var platform = new WindowsBrokerAuthenticodePlatform(
            new TrustSessionFactory(session));

        Result<string> result = platform.VerifyAndGetSignerThumbprint(
            @"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe");

        Assert.True(result.IsFailure);
        Assert.Equal(0, session.SignerCalls);
        Assert.Equal(1, session.DisposeCalls);
    }

    [Fact]
    public void ProductionBrokerClient_ComposesTheAuthenticodeVerifierBeforeLauncher()
    {
        ElevationBrokerClient client = ElevationBrokerClient.CreateProduction();

        Assert.IsType<WindowsBrokerAuthenticodeVerifier>(
            GetPrivateField<IBrokerAuthenticodeVerifier>(client, "_authenticode"));
        Assert.IsType<WindowsBrokerPathResolver>(
            GetPrivateField<IBrokerPathResolver>(client, "_pathResolver"));
        Assert.IsType<WindowsConsentElevationLauncher>(
            GetPrivateField<IConsentElevationLauncher>(client, "_launcher"));
    }

    private static void AssertPathFailure(Result result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED, result.Error!.Code);
        Assert.Equal(
            "The elevated broker installation could not be verified.",
            result.Error.Message);
        Assert.Equal(ErrorCategory.UnrecoverableError, result.Error.Category);
    }

    private static T GetPrivateField<T>(object instance, string fieldName) =>
        (T)instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private sealed class AuthenticodePlatform(
        bool signatureValid,
        string? signer,
        int? failOnCall = null)
        : IBrokerAuthenticodePlatform
    {
        internal int Calls { get; private set; }

        internal List<string> Paths { get; } = [];

        public Result<string> VerifyAndGetSignerThumbprint(string brokerPath)
        {
            Calls++;
            Paths.Add(brokerPath);
            return !signatureValid || signer is null || Calls == failOnCall
                ? Result<string>.Failure(new Error(
                    BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED,
                    "The elevated broker installation could not be verified.",
                    ErrorCategory.UnrecoverableError))
                : Result<string>.Success(signer);
        }
    }

    private sealed class TrustSessionFactory(IAuthenticodeTrustSession session)
        : IAuthenticodeTrustSessionFactory
    {
        public IAuthenticodeTrustSession Open(string brokerPath) => session;
    }

    private sealed class TrustSession(bool trusted, string signer)
        : IAuthenticodeTrustSession
    {
        internal int SignerCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal bool SignerReadBeforeDispose { get; private set; }

        public bool IsTrusted => trusted;

        public Result<string> GetSignerThumbprint()
        {
            SignerCalls++;
            SignerReadBeforeDispose = DisposeCalls == 0;
            return Result<string>.Success(signer);
        }

        public void Dispose() => DisposeCalls++;
    }
}
