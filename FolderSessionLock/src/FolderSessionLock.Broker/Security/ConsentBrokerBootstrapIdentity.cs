using System.Runtime.InteropServices;
using System.Security.Principal;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Security;

internal sealed class ConsentBrokerBootstrapIdentity : IDisposable
{
    internal ConsentBrokerBootstrapIdentity(
        SessionIdentity initiatingClient,
        SessionIdentity broker,
        SecurityIdentifier initiatingLogonSid,
        SecurityIdentifier brokerAccountSid,
        SafeAccessTokenHandle initiatingToken)
    {
        InitiatingClient = initiatingClient;
        Broker = broker;
        InitiatingLogonSid = initiatingLogonSid;
        BrokerAccountSid = brokerAccountSid;
        InitiatingToken = initiatingToken;
    }

    internal SessionIdentity InitiatingClient { get; }

    internal SessionIdentity Broker { get; }

    internal SecurityIdentifier InitiatingLogonSid { get; }

    internal SecurityIdentifier BrokerAccountSid { get; }

    internal SafeAccessTokenHandle InitiatingToken { get; }

    public void Dispose() => InitiatingToken.Dispose();
}

internal sealed record InitiatingClientTokenSnapshot(
    SessionIdentity Identity,
    SafeAccessTokenHandle Token);

internal sealed record ConsentBrokerBootstrapIdentityResult(
    ConsentBrokerBootstrapIdentity? Identity,
    ConsentBrokerExitCode ExitCode)
{
    internal bool IsSuccess => Identity is not null;

    internal static ConsentBrokerBootstrapIdentityResult Success(
        ConsentBrokerBootstrapIdentity identity) => new(
            identity,
            ConsentBrokerExitCode.ProtocolHandledOrLifecycleCompleted);

    internal static ConsentBrokerBootstrapIdentityResult Failure(
        ConsentBrokerExitCode exitCode) => new(null, exitCode);
}

internal interface IConsentBrokerBootstrapIdentityVerifier
{
    ValueTask<ConsentBrokerBootstrapIdentityResult> VerifyAsync(
        BrokerConsentOptions options,
        CancellationToken cancellationToken);
}

internal interface IConsentBrokerBootstrapIdentityPlatform
{
    ValueTask<Result<SessionIdentity>> ReadBrokerIdentityAsync(
        CancellationToken cancellationToken);

    Result<IInitiatingClientProcess> OpenInitiatingClientProcess(uint processId);
}

internal interface IInitiatingClientProcess : IDisposable
{
    bool IsAlive { get; }

    Result<ulong> ReadCreationFileTime();

    Result<InitiatingClientTokenSnapshot> ReadIdentityAndRetainToken();
}

internal sealed class ConsentBrokerBootstrapIdentityVerifier
    : IConsentBrokerBootstrapIdentityVerifier
{
    private readonly IConsentBrokerBootstrapIdentityPlatform _platform;

    internal ConsentBrokerBootstrapIdentityVerifier()
        : this(new WindowsConsentBrokerBootstrapIdentityPlatform())
    {
    }

    internal ConsentBrokerBootstrapIdentityVerifier(
        IConsentBrokerBootstrapIdentityPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public async ValueTask<ConsentBrokerBootstrapIdentityResult> VerifyAsync(
        BrokerConsentOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        Result<SessionIdentity> broker = await _platform
            .ReadBrokerIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (broker.IsFailure)
        {
            return IdentityUnavailable();
        }

        Result<IInitiatingClientProcess> open =
            _platform.OpenInitiatingClientProcess(options.ClientProcessId);
        if (open.IsFailure)
        {
            return IdentityUnavailable();
        }

        using IInitiatingClientProcess process = open.Value;
        if (!process.IsAlive)
        {
            return IdentityUnavailable();
        }

        Result<ulong> creationTime = process.ReadCreationFileTime();
        if (creationTime.IsFailure)
        {
            return IdentityUnavailable();
        }

        if (creationTime.Value != options.ClientProcessCreationFileTime)
        {
            return ConsentBrokerBootstrapIdentityResult.Failure(
                ConsentBrokerExitCode.InitiatingClientProcessMismatch);
        }

        Result<InitiatingClientTokenSnapshot> client =
            process.ReadIdentityAndRetainToken();
        if (client.IsFailure)
        {
            return IdentityUnavailable();
        }

        if (!string.Equals(
            client.Value.Identity.AccountSid,
            broker.Value.AccountSid,
            StringComparison.Ordinal))
        {
            client.Value.Token.Dispose();
            return ConsentBrokerBootstrapIdentityResult.Failure(
                ConsentBrokerExitCode.CrossAccountElevationNotSupported);
        }

        if (client.Value.Identity.WindowsSessionId < 0
            || broker.Value.WindowsSessionId < 0
            || checked((uint)client.Value.Identity.WindowsSessionId) != options.SessionId
            || checked((uint)broker.Value.WindowsSessionId) != options.SessionId)
        {
            client.Value.Token.Dispose();
            return IdentityUnavailable();
        }

        try
        {
            return ConsentBrokerBootstrapIdentityResult.Success(new(
                client.Value.Identity,
                broker.Value,
                new SecurityIdentifier(client.Value.Identity.LogonSid),
                new SecurityIdentifier(broker.Value.AccountSid),
                client.Value.Token));
        }
        catch (ArgumentException)
        {
            client.Value.Token.Dispose();
            return IdentityUnavailable();
        }
    }

    private static ConsentBrokerBootstrapIdentityResult IdentityUnavailable() =>
        ConsentBrokerBootstrapIdentityResult.Failure(
            ConsentBrokerExitCode.InitiatingClientIdentityUnavailable);
}

internal sealed class WindowsConsentBrokerBootstrapIdentityPlatform
    : IConsentBrokerBootstrapIdentityPlatform
{
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint TokenQuery = 0x0008;
    private const uint StillActive = 259;

    public ValueTask<Result<SessionIdentity>> ReadBrokerIdentityAsync(
        CancellationToken cancellationToken) =>
        new WindowsSessionIdentityProvider().GetCurrentAsync(cancellationToken);

    public Result<IInitiatingClientProcess> OpenInitiatingClientProcess(uint processId)
    {
        SafeProcessHandle handle = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        return handle.IsInvalid
            ? Failure<IInitiatingClientProcess>(handle)
            : Result<IInitiatingClientProcess>.Success(
                new WindowsInitiatingClientProcess(handle));
    }

    private static Result<T> Failure<T>(SafeProcessHandle handle)
    {
        handle.Dispose();
        return Result<T>.Failure(IdentityError());
    }

    private static Error IdentityError() => new(
        BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE,
        "The client identity could not be verified.",
        ErrorCategory.UnrecoverableError);

    private sealed class WindowsInitiatingClientProcess(SafeProcessHandle handle)
        : IInitiatingClientProcess
    {
        private readonly SafeProcessHandle _handle = handle;
        private readonly WindowsAccessTokenIdentityReader _identityReader = new();

        public bool IsAlive => GetExitCodeProcess(_handle, out uint exitCode)
            && exitCode == StillActive;

        public Result<ulong> ReadCreationFileTime()
        {
            if (!GetProcessTimes(
                _handle,
                out FileTime creationTime,
                out _,
                out _,
                out _))
            {
                return Result<ulong>.Failure(IdentityError());
            }

            ulong value = ((ulong)creationTime.HighDateTime << 32)
                | creationTime.LowDateTime;
            return value == 0
                ? Result<ulong>.Failure(IdentityError())
                : Result<ulong>.Success(value);
        }

        public Result<InitiatingClientTokenSnapshot> ReadIdentityAndRetainToken()
        {
            if (!OpenProcessToken(_handle, TokenQuery, out SafeAccessTokenHandle token))
            {
                return Result<InitiatingClientTokenSnapshot>.Failure(IdentityError());
            }

            Result<SessionIdentity> identity = _identityReader.Read(token);
            if (identity.IsFailure)
            {
                token.Dispose();
                return Result<InitiatingClientTokenSnapshot>.Failure(identity.Error!);
            }

            return Result<InitiatingClientTokenSnapshot>.Success(new(identity.Value, token));
        }

        public void Dispose() => _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(
        SafeProcessHandle process,
        out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);
}
