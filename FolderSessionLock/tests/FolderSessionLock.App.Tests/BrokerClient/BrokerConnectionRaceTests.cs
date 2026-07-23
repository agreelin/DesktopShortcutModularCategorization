using FolderSessionLock.App.BrokerClient;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.App.Tests.BrokerClient;

public sealed class BrokerConnectionRaceTests
{
    [Fact]
    public async Task ConnectAsync_ReturnsThePipeWithoutTerminatingTheConnectedProcess()
    {
        var pipe = new MemoryStream();
        var process = new BrokerProcess((_, token) => WaitUntilCancelled(token));
        var race = new BrokerConnectionRace(new ImmediateConnector(pipe));

        BrokerConnectionResult result = await race.ConnectAsync(process, default);

        Assert.True(result.IsConnected);
        Assert.Same(pipe, result.Pipe);
        Assert.Same(process, result.Process);
        Assert.Equal(0, process.TerminateCalls);
        Assert.Equal(0, process.DisposeCalls);
        result.Pipe!.Dispose();
        result.Process!.Dispose();
    }

    [Theory]
    [InlineData(2, "FSL_E_BROKER_LAUNCH_CONTRACT_INVALID", false)]
    [InlineData(20, "FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED", false)]
    [InlineData(21, "FSL_E_CLIENT_IDENTITY_UNAVAILABLE", false)]
    [InlineData(22, "FSL_E_CLIENT_PROCESS_MISMATCH", false)]
    [InlineData(23, "FSL_E_PIPE_INITIALIZATION_FAILED", false)]
    [InlineData(24, "FSL_E_BROKER_CONNECT_TIMEOUT", true)]
    [InlineData(28, "FSL_E_PROTECTED_LOGGER_UNAVAILABLE", false)]
    [InlineData(99, "FSL_E_BROKER_EXITED_EARLY", false)]
    public async Task ConnectAsync_MapsProcessExitBeforeConnection(
        int exitCode,
        string expectedCode,
        bool retryable)
    {
        var process = new BrokerProcess((_, _) => ValueTask.FromResult(exitCode));
        var race = new BrokerConnectionRace(new NeverConnector());

        BrokerConnectionResult result = await race.ConnectAsync(process, default);

        Assert.False(result.IsConnected);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(retryable, result.Error.Retryable);
        Assert.Equal(1, process.DisposeCalls);
        Assert.Equal(0, process.TerminateCalls);
    }

    [Fact]
    public async Task ConnectAsync_TimeoutTerminatesWithExit29AndRequiresExitProof()
    {
        var process = new BrokerProcess((call, token) => call == 1
            ? WaitUntilCancelled(token)
            : ValueTask.FromResult(29));
        var race = new BrokerConnectionRace(
            new NeverConnector(),
            (_, _) => Task.CompletedTask);

        BrokerConnectionResult result = await race.ConnectAsync(process, default);

        Assert.Equal(BrokerErrorCodes.FSL_E_BROKER_CONNECT_TIMEOUT, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.Equal(1, process.TerminateCalls);
        Assert.Equal(29u, process.LastTerminationExitCode);
        Assert.Equal(2, process.WaitCalls);
        Assert.Equal(1, process.DisposeCalls);
    }

    [Fact]
    public async Task ConnectAsync_TerminationFailureReturnsCleanupFailure()
    {
        var process = new BrokerProcess(
            (_, token) => WaitUntilCancelled(token),
            Result.Failure(new Error(
                BrokerErrorCodes.FSL_E_BROKER_PROCESS_CLEANUP_FAILED,
                "The unused elevated broker process could not be cleaned up safely.",
                ErrorCategory.UnrecoverableError)));
        var race = new BrokerConnectionRace(
            new NeverConnector(),
            (_, _) => Task.CompletedTask);

        BrokerConnectionResult result = await race.ConnectAsync(process, default);

        AssertCleanupFailure(result);
        Assert.Equal(1, process.TerminateCalls);
        Assert.Equal(1, process.DisposeCalls);
    }

    [Fact]
    public async Task ConnectAsync_WrongTerminationExitCodeReturnsCleanupFailure()
    {
        var process = new BrokerProcess((call, token) => call == 1
            ? WaitUntilCancelled(token)
            : ValueTask.FromResult(0));
        var race = new BrokerConnectionRace(
            new NeverConnector(),
            (_, _) => Task.CompletedTask);

        BrokerConnectionResult result = await race.ConnectAsync(process, default);

        AssertCleanupFailure(result);
        Assert.Equal(1, process.TerminateCalls);
        Assert.Equal(2, process.WaitCalls);
        Assert.Equal(1, process.DisposeCalls);
    }

    [Fact]
    public void ExitCodeMapper_UsesExactPublicMessagesForDefinedMappings()
    {
        AssertError(
            BrokerExitCodeMapper.Map(2),
            BrokerErrorCodes.FSL_E_BROKER_LAUNCH_CONTRACT_INVALID,
            "The elevated broker launch request is invalid.");
        AssertError(
            BrokerExitCodeMapper.Map(20),
            BrokerErrorCodes.FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED,
            "Cross-account elevation is not supported.");
        AssertError(
            BrokerExitCodeMapper.Map(21),
            BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE,
            "The client identity could not be verified.");
        AssertError(
            BrokerExitCodeMapper.Map(22),
            BrokerErrorCodes.FSL_E_CLIENT_PROCESS_MISMATCH,
            "The connected client process does not match the handshake.");
        AssertError(
            BrokerExitCodeMapper.Map(23),
            BrokerErrorCodes.FSL_E_PIPE_INITIALIZATION_FAILED,
            "The elevated broker could not create its secure communication endpoint.");
        AssertError(
            BrokerExitCodeMapper.Map(24),
            BrokerErrorCodes.FSL_E_BROKER_CONNECT_TIMEOUT,
            "The elevated broker did not establish a secure connection in time.");
        AssertError(
            BrokerExitCodeMapper.Map(28),
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            "The protected diagnostic logger could not be initialized.");
        AssertError(
            BrokerExitCodeMapper.Map(99),
            BrokerErrorCodes.FSL_E_BROKER_EXITED_EARLY,
            "The elevated broker exited before a secure connection was established.");
    }

    private static async ValueTask<int> WaitUntilCancelled(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    private static void AssertCleanupFailure(BrokerConnectionResult result)
    {
        Assert.False(result.IsConnected);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_BROKER_PROCESS_CLEANUP_FAILED,
            result.Error!.Code);
        Assert.Equal(
            "The unused elevated broker process could not be cleaned up safely.",
            result.Error.Message);
        Assert.False(result.Error.Retryable);
        Assert.Null(result.Error.Field);
    }

    private static void AssertError(BrokerError error, string code, string message)
    {
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
        Assert.Null(error.Field);
    }

    private sealed class ImmediateConnector(Stream pipe) : IBrokerPipeConnector
    {
        public ValueTask<Result<Stream>> ConnectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<Stream>.Success(pipe));
    }

    private sealed class NeverConnector : IBrokerPipeConnector
    {
        public async ValueTask<Result<Stream>> ConnectAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class BrokerProcess(
        Func<int, CancellationToken, ValueTask<int>> wait,
        Result? termination = null) : IBrokerProcessHandle
    {
        internal int WaitCalls { get; private set; }

        internal int TerminateCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal uint? LastTerminationExitCode { get; private set; }

        public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCalls++;
            return wait(WaitCalls, cancellationToken);
        }

        public Result<int> GetExitCode() => Result<int>.Success(0);

        public Result Terminate(uint exitCode)
        {
            TerminateCalls++;
            LastTerminationExitCode = exitCode;
            return termination ?? Result.Success();
        }

        public void Dispose() => DisposeCalls++;
    }
}
