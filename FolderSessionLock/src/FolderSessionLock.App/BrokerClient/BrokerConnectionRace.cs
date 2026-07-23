using System.IO;
using System.IO.Pipes;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.App.BrokerClient;

internal interface IBrokerPipeConnector
{
    ValueTask<Result<Stream>> ConnectAsync(CancellationToken cancellationToken);
}

internal sealed record BrokerConnectionResult(
    Stream? Pipe,
    IBrokerProcessHandle? Process,
    BrokerError? Error)
{
    internal bool IsConnected => Pipe is not null;
}

internal sealed class BrokerConnectionRace
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly IBrokerPipeConnector _connector;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal BrokerConnectionRace(
        IBrokerPipeConnector connector,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _delay = delay ?? Task.Delay;
    }

    internal async ValueTask<BrokerConnectionResult> ConnectAsync(
        IBrokerProcessHandle process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<Result<Stream>> connectTask = _connector.ConnectAsync(raceCancellation.Token).AsTask();
        Task<int> exitTask = process.WaitForExitAsync(raceCancellation.Token).AsTask();
        Task timeoutTask = _delay(ConnectTimeout, cancellationToken);
        Task completed = await Task.WhenAny(connectTask, exitTask, timeoutTask);

        if (connectTask.IsCompletedSuccessfully && connectTask.Result.IsSuccess)
        {
            raceCancellation.Cancel();
            return new BrokerConnectionResult(connectTask.Result.Value, process, null);
        }

        if (completed == exitTask)
        {
            raceCancellation.Cancel();
            try
            {
                int exitCode = await exitTask;
                process.Dispose();
                return new BrokerConnectionResult(null, null, BrokerExitCodeMapper.Map(exitCode));
            }
            catch (Exception exception) when (
                exception is IOException
                    or OperationCanceledException
                    or System.ComponentModel.Win32Exception)
            {
                process.Dispose();
                return new BrokerConnectionResult(null, null, BrokerExitCodeMapper.ExitedEarly());
            }
        }

        if (completed == connectTask)
        {
            raceCancellation.Cancel();
            process.Dispose();
            return new BrokerConnectionResult(null, null, BrokerExitCodeMapper.ExitedEarly());
        }

        raceCancellation.Cancel();
        Result termination = process.Terminate(
            (uint)ConsentBrokerExitCode.LauncherTerminatedBeforeConnect);
        if (termination.IsFailure)
        {
            process.Dispose();
            return CleanupFailed();
        }

        using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
        try
        {
            int exitCode = await process.WaitForExitAsync(cleanupCancellation.Token);
            process.Dispose();
            return exitCode == (int)ConsentBrokerExitCode.LauncherTerminatedBeforeConnect
                ? new BrokerConnectionResult(null, null, BrokerExitCodeMapper.ConnectTimeout())
                : CleanupFailed();
        }
        catch (Exception exception) when (
            exception is IOException
                or OperationCanceledException
                or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            return CleanupFailed();
        }
    }

    private static BrokerConnectionResult CleanupFailed() => new(
        null,
        null,
        new BrokerError(
            BrokerErrorCodes.FSL_E_BROKER_PROCESS_CLEANUP_FAILED,
            "The unused elevated broker process could not be cleaned up safely.",
            false,
            null));
}

internal sealed class WindowsBrokerPipeConnector : IBrokerPipeConnector
{
    public async ValueTask<Result<Stream>> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            BrokerProtocolConstants.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken);
            return Result<Stream>.Success(pipe);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException)
        {
            pipe.Dispose();
            return Result<Stream>.Failure(new Error(
                BrokerErrorCodes.FSL_E_BROKER_EXITED_EARLY,
                "The elevated broker exited before a secure connection was established.",
                ErrorCategory.UnrecoverableError));
        }
    }
}

internal static class BrokerExitCodeMapper
{
    internal static BrokerError Map(int exitCode) => exitCode switch
    {
        (int)ConsentBrokerExitCode.InvalidArguments => new(
            BrokerErrorCodes.FSL_E_BROKER_LAUNCH_CONTRACT_INVALID,
            "The elevated broker launch request is invalid.",
            false,
            null),
        (int)ConsentBrokerExitCode.CrossAccountElevationNotSupported => new(
            BrokerErrorCodes.FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED,
            "Cross-account elevation is not supported.",
            false,
            null),
        (int)ConsentBrokerExitCode.InitiatingClientIdentityUnavailable => new(
            BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE,
            "The client identity could not be verified.",
            false,
            null),
        (int)ConsentBrokerExitCode.InitiatingClientProcessMismatch => new(
            BrokerErrorCodes.FSL_E_CLIENT_PROCESS_MISMATCH,
            "The connected client process does not match the handshake.",
            false,
            null),
        (int)ConsentBrokerExitCode.PipeInitializationFailed => new(
            BrokerErrorCodes.FSL_E_PIPE_INITIALIZATION_FAILED,
            "The elevated broker could not create its secure communication endpoint.",
            false,
            null),
        (int)ConsentBrokerExitCode.ClientConnectTimeout => ConnectTimeout(),
        (int)ConsentBrokerExitCode.ProtectedLoggerUnavailableOrInternalFailure => new(
            BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
            "The protected diagnostic logger could not be initialized.",
            false,
            null),
        _ => ExitedEarly(),
    };

    internal static BrokerError ConnectTimeout() => new(
        BrokerErrorCodes.FSL_E_BROKER_CONNECT_TIMEOUT,
        "The elevated broker did not establish a secure connection in time.",
        true,
        null);

    internal static BrokerError ExitedEarly() => new(
        BrokerErrorCodes.FSL_E_BROKER_EXITED_EARLY,
        "The elevated broker exited before a secure connection was established.",
        false,
        null);
}
