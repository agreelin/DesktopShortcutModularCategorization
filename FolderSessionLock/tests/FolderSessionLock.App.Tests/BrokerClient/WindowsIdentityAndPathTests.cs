using FolderSessionLock.App.BrokerClient;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.App.Tests.BrokerClient;

public sealed class WindowsIdentityAndPathTests
{
    private const uint SeGroupLogonId = 0xC0000000;

    [Fact]
    public void SelectUniqueLogonSid_RequiresExactlyOneLogonGroup()
    {
        Result<string> one = WindowsInitiatingClientIdentityPlatform.SelectUniqueLogonSid(
        [
            new("S-1-5-32-545", 0),
            new("S-1-5-5-1-2", SeGroupLogonId),
        ]);
        Result<string> none = WindowsInitiatingClientIdentityPlatform.SelectUniqueLogonSid(
        [
            new("S-1-5-32-545", 0),
        ]);
        Result<string> multiple = WindowsInitiatingClientIdentityPlatform.SelectUniqueLogonSid(
        [
            new("S-1-5-5-1-2", SeGroupLogonId),
            new("S-1-5-5-3-4", SeGroupLogonId),
        ]);

        Assert.True(one.IsSuccess);
        Assert.Equal("S-1-5-5-1-2", one.Value);
        AssertIdentityFailure(none);
        AssertIdentityFailure(multiple);
    }

    [Fact]
    public void IdentityProvider_CapturesTheExactPlatformSnapshot()
    {
        var platform = new IdentityPlatform(
            Result<uint>.Success(42),
            Result<ulong>.Success(123456789),
            Result<InitiatingTokenIdentity>.Success(new(
                "S-1-5-21-1",
                "S-1-5-5-1-2",
                7)));
        var provider = new WindowsInitiatingClientIdentityProvider(platform);

        Result<InitiatingClientIdentity> result = provider.Capture();

        Assert.True(result.IsSuccess);
        Assert.Equal(42u, result.Value.ProcessId);
        Assert.Equal(123456789ul, result.Value.ProcessCreationFileTime);
        Assert.Equal("S-1-5-21-1", result.Value.AccountSid);
        Assert.Equal("S-1-5-5-1-2", result.Value.LogonSid);
        Assert.Equal(7u, result.Value.WindowsSessionId);
    }

    [Fact]
    public void IdentityProvider_FailsClosedWhenAnyRequiredValueIsUnavailable()
    {
        Error error = IdentityError();
        var providers = new[]
        {
            new WindowsInitiatingClientIdentityProvider(new IdentityPlatform(
                Result<uint>.Failure(error),
                Result<ulong>.Success(1),
                TokenSuccess())),
            new WindowsInitiatingClientIdentityProvider(new IdentityPlatform(
                Result<uint>.Success(1),
                Result<ulong>.Failure(error),
                TokenSuccess())),
            new WindowsInitiatingClientIdentityProvider(new IdentityPlatform(
                Result<uint>.Success(1),
                Result<ulong>.Success(1),
                Result<InitiatingTokenIdentity>.Failure(error))),
            new WindowsInitiatingClientIdentityProvider(new IdentityPlatform(
                Result<uint>.Success(0),
                Result<ulong>.Success(1),
                TokenSuccess())),
            new WindowsInitiatingClientIdentityProvider(new IdentityPlatform(
                Result<uint>.Success(1),
                Result<ulong>.Success(0),
                TokenSuccess())),
        };

        foreach (WindowsInitiatingClientIdentityProvider provider in providers)
        {
            Result<InitiatingClientIdentity> result = provider.Capture();
            AssertIdentityFailure(result);
        }
    }

    [Fact]
    public void BrokerPathResolver_UsesOnlyTheFixedProgramFilesLocation()
    {
        string programFiles = Path.GetFullPath(@"C:\Program Files");
        var platform = new BrokerPathPlatform(
            Result<string>.Success(programFiles),
            Result<BrokerFileIdentity>.Success(new(1, 2, 3)));
        var resolver = new WindowsBrokerPathResolver(platform);

        Result<ResolvedBrokerPath> result = resolver.Resolve();

        string installation = Path.Combine(programFiles, "FolderSessionLock");
        string broker = Path.Combine(installation, "FolderSessionLock.Broker.exe");
        Assert.True(result.IsSuccess);
        Assert.Equal(installation, result.Value.InstallationDirectory);
        Assert.Equal(broker, result.Value.BrokerPath);
        Assert.Equal(new BrokerFileIdentity(1, 2, 3), result.Value.Identity);
        Assert.Equal(installation, platform.VerifiedInstallationDirectory);
        Assert.Equal(broker, platform.VerifiedBrokerPath);
    }

    [Fact]
    public void BrokerPathResolver_RejectsRelativeOrUnverifiedLocations()
    {
        var relativePlatform = new BrokerPathPlatform(
            Result<string>.Success("relative"),
            Result<BrokerFileIdentity>.Success(new(1, 2, 3)));
        var verificationFailure = new BrokerPathPlatform(
            Result<string>.Success(Path.GetFullPath(@"C:\Program Files")),
            Result<BrokerFileIdentity>.Failure(PathError()));

        Result<ResolvedBrokerPath> relative =
            new WindowsBrokerPathResolver(relativePlatform).Resolve();
        Result<ResolvedBrokerPath> unverified =
            new WindowsBrokerPathResolver(verificationFailure).Resolve();

        AssertPathFailure(relative);
        Assert.Null(relativePlatform.VerifiedBrokerPath);
        AssertPathFailure(unverified);
    }

    private static Result<InitiatingTokenIdentity> TokenSuccess() =>
        Result<InitiatingTokenIdentity>.Success(new(
            "S-1-5-21-1",
            "S-1-5-5-1-2",
            7));

    private static Error IdentityError() => new(
        BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE,
        "The client identity could not be verified.",
        ErrorCategory.UnrecoverableError);

    private static Error PathError() => new(
        BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED,
        "The elevated broker installation could not be verified.",
        ErrorCategory.UnrecoverableError);

    private static void AssertIdentityFailure<T>(Result<T> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE, result.Error!.Code);
        Assert.Equal("The client identity could not be verified.", result.Error.Message);
    }

    private static void AssertPathFailure(Result<ResolvedBrokerPath> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED, result.Error!.Code);
        Assert.Equal(
            "The elevated broker installation could not be verified.",
            result.Error.Message);
    }

    private sealed class IdentityPlatform(
        Result<uint> processId,
        Result<ulong> creationTime,
        Result<InitiatingTokenIdentity> token) : IInitiatingClientIdentityPlatform
    {
        public Result<uint> GetCurrentProcessId() => processId;

        public Result<ulong> GetCurrentProcessCreationFileTime() => creationTime;

        public Result<InitiatingTokenIdentity> ReadCurrentProcessToken() => token;
    }

    private sealed class BrokerPathPlatform(
        Result<string> programFiles,
        Result<BrokerFileIdentity> verification) : IBrokerPathPlatform
    {
        internal string? VerifiedInstallationDirectory { get; private set; }

        internal string? VerifiedBrokerPath { get; private set; }

        public Result<string> GetProgramFilesPath() => programFiles;

        public Result<BrokerFileIdentity> Verify(
            string installationDirectory,
            string brokerPath)
        {
            VerifiedInstallationDirectory = installationDirectory;
            VerifiedBrokerPath = brokerPath;
            return verification;
        }
    }
}
