using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.App.BrokerClient;

internal sealed record ConsentElevationLaunchRequest(
    ResolvedBrokerPath Broker,
    Guid RequestId,
    InitiatingClientIdentity ClientIdentity,
    nint OwnerWindow);

internal interface IConsentElevationLauncher
{
    ValueTask<Result<IBrokerProcessHandle>> LaunchAsync(
        ConsentElevationLaunchRequest request);
}

internal interface IBrokerProcessHandle : IDisposable
{
    ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken);

    Result<int> GetExitCode();

    Result Terminate(uint exitCode);
}

internal sealed record ConsentShellExecuteRequest(
    uint Mask,
    nint Window,
    string Verb,
    string File,
    string Parameters,
    string Directory,
    int Show);

internal sealed record ConsentShellExecuteResult(
    bool Success,
    int ErrorCode,
    IBrokerProcessHandle? Process);

internal interface IConsentElevationPlatform
{
    ConsentShellExecuteResult Execute(ConsentShellExecuteRequest request);
}

internal sealed class WindowsConsentElevationLauncher : IConsentElevationLauncher
{
    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const uint SeeMaskNoAsync = 0x00000100;
    private const uint SeeMaskFlagNoUi = 0x00000400;
    private const uint SeeMaskUnicode = 0x00004000;
    private const int SwHide = 0;
    private const int ErrorCancelled = 1223;
    private readonly IConsentElevationPlatform _platform;

    internal WindowsConsentElevationLauncher()
        : this(new WindowsConsentElevationPlatform())
    {
    }

    internal WindowsConsentElevationLauncher(IConsentElevationPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public ValueTask<Result<IBrokerProcessHandle>> LaunchAsync(
        ConsentElevationLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<Result<IBrokerProcessHandle>>(Task.Run(() => Launch(request)));
    }

    internal static string CreateParameters(
        Guid requestId,
        InitiatingClientIdentity identity)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("The request ID must not be empty.", nameof(requestId));
        }

        string[] arguments =
        [
            "--mode",
            "consent-broker",
            "--pipe-name",
            BrokerProtocolConstants.PipeName,
            "--session-id",
            identity.WindowsSessionId.ToString(CultureInfo.InvariantCulture),
            "--request-id",
            requestId.ToString("D"),
            "--client-process-id",
            identity.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--client-process-creation-filetime",
            identity.ProcessCreationFileTime.ToString(CultureInfo.InvariantCulture),
        ];
        return string.Join(' ', arguments.Select(WindowsCommandLineArgumentEncoder.Encode));
    }

    private Result<IBrokerProcessHandle> Launch(ConsentElevationLaunchRequest request)
    {
        if (!Path.IsPathFullyQualified(request.Broker.BrokerPath)
            || !Path.IsPathFullyQualified(request.Broker.InstallationDirectory))
        {
            return LaunchFailure();
        }

        ConsentShellExecuteResult result = _platform.Execute(new(
            SeeMaskNoCloseProcess | SeeMaskNoAsync | SeeMaskFlagNoUi | SeeMaskUnicode,
            request.OwnerWindow,
            "runas",
            request.Broker.BrokerPath,
            CreateParameters(request.RequestId, request.ClientIdentity),
            request.Broker.InstallationDirectory,
            SwHide));
        if (!result.Success)
        {
            return result.ErrorCode == ErrorCancelled
                ? Result<IBrokerProcessHandle>.Failure(new Error(
                    BrokerErrorCodes.FSL_E_ELEVATION_CANCELLED,
                    "The elevation request was cancelled.",
                    ErrorCategory.RecoverableError))
                : LaunchFailure();
        }

        return result.Process is null
            ? LaunchFailure()
            : Result<IBrokerProcessHandle>.Success(result.Process);
    }

    private static Result<IBrokerProcessHandle> LaunchFailure() =>
        Result<IBrokerProcessHandle>.Failure(new Error(
            BrokerErrorCodes.FSL_E_ELEVATION_LAUNCH_FAILED,
            "The elevated broker could not be started.",
            ErrorCategory.UnrecoverableError));

}

internal sealed class WindowsConsentElevationPlatform : IConsentElevationPlatform
{
    public ConsentShellExecuteResult Execute(ConsentShellExecuteRequest request)
    {
        var information = new ShellExecuteInfo
        {
            Size = checked((uint)Marshal.SizeOf<ShellExecuteInfo>()),
            Mask = request.Mask,
            Window = request.Window,
            Verb = request.Verb,
            File = request.File,
            Parameters = request.Parameters,
            Directory = request.Directory,
            Show = request.Show,
        };
        if (!ShellExecuteEx(ref information))
        {
            return new(false, Marshal.GetLastPInvokeError(), null);
        }

        return new(
            true,
            0,
            information.Process == nint.Zero
                ? null
                : new WindowsBrokerProcessHandle(
                    new SafeProcessHandle(information.Process, ownsHandle: true)));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        internal uint Size;
        internal uint Mask;
        internal nint Window;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? File;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Directory;
        internal int Show;
        internal nint Instance;
        internal nint IdList;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Class;
        internal nint ClassKey;
        internal uint HotKey;
        internal nint IconOrMonitor;
        internal nint Process;
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo information);
}

internal static class WindowsCommandLineArgumentEncoder
{
    internal static string Encode(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length != 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var encoded = new StringBuilder(argument.Length + 2);
        encoded.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                encoded.Append('\\', (backslashes * 2) + 1);
                encoded.Append('"');
                backslashes = 0;
                continue;
            }

            encoded.Append('\\', backslashes);
            backslashes = 0;
            encoded.Append(character);
        }

        encoded.Append('\\', backslashes * 2);
        encoded.Append('"');
        return encoded.ToString();
    }
}

internal sealed class WindowsBrokerProcessHandle(SafeProcessHandle handle)
    : IBrokerProcessHandle
{
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private readonly SafeProcessHandle _handle = handle;

    public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint result = WaitForSingleObject(_handle, 50);
                if (result == WaitObject0)
                {
                    return;
                }

                if (result != WaitTimeout)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
        }, cancellationToken);
        Result<int> exit = GetExitCode();
        return exit.IsSuccess ? exit.Value : throw new Win32Exception();
    }

    public Result<int> GetExitCode() => GetExitCodeProcess(_handle, out uint code)
        ? Result<int>.Success(unchecked((int)code))
        : Failure<int>();

    public Result Terminate(uint exitCode) => TerminateProcess(_handle, exitCode)
        ? Result.Success()
        : Result.Failure(FailureError());

    public void Dispose() => _handle.Dispose();

    private static Result<T> Failure<T>() => Result<T>.Failure(FailureError());

    private static Error FailureError() => new(
        BrokerErrorCodes.FSL_E_BROKER_PROCESS_CLEANUP_FAILED,
        "The unused elevated broker process could not be cleaned up safely.",
        ErrorCategory.UnrecoverableError);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
