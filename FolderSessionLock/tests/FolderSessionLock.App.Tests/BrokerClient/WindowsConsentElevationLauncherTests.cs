using FolderSessionLock.App.BrokerClient;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.App.Tests.BrokerClient;

public sealed class WindowsConsentElevationLauncherTests
{
    private static readonly Guid RequestId =
        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    [Fact]
    public void CreateParameters_UsesTheExactTwelveArgumentsAndContainsNoSid()
    {
        string parameters = WindowsConsentElevationLauncher.CreateParameters(
            RequestId,
            Identity());

        Assert.Equal(
            "--mode consent-broker --pipe-name FolderSessionLock.Broker.v1 "
            + "--session-id 7 --request-id aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee "
            + "--client-process-id 42 --client-process-creation-filetime 123456789",
            parameters);
        Assert.DoesNotContain("S-1-5-21-1", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-5-1-2", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("--account-sid", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("--logon-sid", parameters, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a b\\", "\"a b\\\\\"")]
    public void CommandLineEncoder_UsesWindowsArgumentQuoting(
        string value,
        string expected)
    {
        Assert.Equal(expected, WindowsCommandLineArgumentEncoder.Encode(value));
    }

    [Fact]
    public async Task LaunchAsync_UsesTheExactShellExecuteContract()
    {
        var process = new BrokerProcess();
        var platform = new ElevationPlatform(new(true, 0, process));
        var launcher = new WindowsConsentElevationLauncher(platform);
        ResolvedBrokerPath broker = BrokerPath();

        Result<IBrokerProcessHandle> result = await launcher.LaunchAsync(new(
            broker,
            RequestId,
            Identity(),
            (nint)123));

        Assert.True(result.IsSuccess);
        Assert.Same(process, result.Value);
        ConsentShellExecuteRequest request = platform.Request!;
        Assert.Equal(0x00004540u, request.Mask);
        Assert.Equal((nint)123, request.Window);
        Assert.Equal("runas", request.Verb);
        Assert.Equal(broker.BrokerPath, request.File);
        Assert.Equal(broker.InstallationDirectory, request.Directory);
        Assert.Equal(0, request.Show);
        Assert.Equal(
            WindowsConsentElevationLauncher.CreateParameters(RequestId, Identity()),
            request.Parameters);
    }

    [Theory]
    [InlineData(1223, "FSL_E_ELEVATION_CANCELLED", "The elevation request was cancelled.", ErrorCategory.RecoverableError)]
    [InlineData(5, "FSL_E_ELEVATION_LAUNCH_FAILED", "The elevated broker could not be started.", ErrorCategory.UnrecoverableError)]
    public async Task LaunchAsync_MapsShellExecuteFailure(
        int errorCode,
        string expectedCode,
        string expectedMessage,
        ErrorCategory expectedCategory)
    {
        var launcher = new WindowsConsentElevationLauncher(
            new ElevationPlatform(new(false, errorCode, null)));

        Result<IBrokerProcessHandle> result = await launcher.LaunchAsync(new(
            BrokerPath(),
            RequestId,
            Identity(),
            nint.Zero));

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(expectedMessage, result.Error.Message);
        Assert.Equal(expectedCategory, result.Error.Category);
    }

    [Fact]
    public async Task LaunchAsync_RejectsSuccessWithoutProcessHandle()
    {
        var launcher = new WindowsConsentElevationLauncher(
            new ElevationPlatform(new(true, 0, null)));

        Result<IBrokerProcessHandle> result = await launcher.LaunchAsync(new(
            BrokerPath(),
            RequestId,
            Identity(),
            nint.Zero));

        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_ELEVATION_LAUNCH_FAILED, result.Error!.Code);
        Assert.Equal("The elevated broker could not be started.", result.Error.Message);
    }

    [Fact]
    public async Task LaunchAsync_RejectsRelativeBrokerPathBeforeShellExecute()
    {
        var platform = new ElevationPlatform(new(true, 0, new BrokerProcess()));
        var launcher = new WindowsConsentElevationLauncher(platform);

        Result<IBrokerProcessHandle> result = await launcher.LaunchAsync(new(
            new("relative", "relative.exe", new(1, 2, 3)),
            RequestId,
            Identity(),
            nint.Zero));

        Assert.True(result.IsFailure);
        Assert.Equal(BrokerErrorCodes.FSL_E_ELEVATION_LAUNCH_FAILED, result.Error!.Code);
        Assert.Null(platform.Request);
    }

    private static InitiatingClientIdentity Identity() => new(
        42,
        123456789,
        "S-1-5-21-1",
        "S-1-5-5-1-2",
        7);

    private static ResolvedBrokerPath BrokerPath()
    {
        string directory = Path.GetFullPath(@"C:\Program Files\FolderSessionLock");
        return new(
            directory,
            Path.Combine(directory, "FolderSessionLock.Broker.exe"),
            new BrokerFileIdentity(1, 2, 3));
    }

    private sealed class ElevationPlatform(ConsentShellExecuteResult result)
        : IConsentElevationPlatform
    {
        internal ConsentShellExecuteRequest? Request { get; private set; }

        public ConsentShellExecuteResult Execute(ConsentShellExecuteRequest request)
        {
            Request = request;
            return result;
        }
    }

    private sealed class BrokerProcess : IBrokerProcessHandle
    {
        public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public Result<int> GetExitCode() => Result<int>.Success(0);

        public Result Terminate(uint exitCode) => Result.Success();

        public void Dispose()
        {
        }
    }
}
