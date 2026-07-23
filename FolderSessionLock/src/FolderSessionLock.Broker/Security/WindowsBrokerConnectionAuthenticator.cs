using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Security;

public sealed class WindowsBrokerConnectionAuthenticator : IBrokerConnectionAuthenticator
{
    private const uint TokenQuery = 0x00000008;
    private readonly WindowsAccessTokenIdentityReader _identityReader = new();
    private readonly IBrokerProcessTerminator _processTerminator;
    private readonly Func<bool> _revertToSelf;

    public WindowsBrokerConnectionAuthenticator()
        : this(new FailFastBrokerProcessTerminator(), RevertToSelf)
    {
    }

    internal WindowsBrokerConnectionAuthenticator(
        IBrokerProcessTerminator processTerminator,
        Func<bool> revertToSelf)
    {
        _processTerminator = processTerminator ?? throw new ArgumentNullException(nameof(processTerminator));
        _revertToSelf = revertToSelf ?? throw new ArgumentNullException(nameof(revertToSelf));
    }

    public async ValueTask<BrokerAuthenticationResult> AuthenticateAsync(
        Stream stream,
        BrokerClientHello hello,
        BrokerConsentOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream is not NamedPipeServerStream pipe
            || !pipe.IsConnected
            || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actualProcessId))
        {
            return IdentityUnavailable();
        }

        if (hello.ClaimedClientProcessId != actualProcessId)
        {
            return Failure(
                BrokerErrorCodes.FSL_E_CLIENT_PROCESS_MISMATCH,
                "The connected client process does not match the handshake.",
                "claimedClientProcessId");
        }

        DateTimeOffset processStartUtc;
        int processSessionId;
        try
        {
            using Process process = Process.GetProcessById(checked((int)actualProcessId));
            if (process.HasExited)
            {
                return IdentityUnavailable();
            }

            processStartUtc = process.StartTime.ToUniversalTime();
            processSessionId = process.SessionId;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        {
            return IdentityUnavailable();
        }

        if (processSessionId < 0
            || checked((uint)processSessionId) != hello.ClientSessionId
            || hello.ClientSessionId != options.SessionId)
        {
            return SessionMismatch();
        }

        Result<SessionIdentity>? clientIdentity = null;
        BrokerAuthenticationResult? impersonationFailure = null;
        bool impersonated = false;
        try
        {
            if (!ImpersonateNamedPipeClient(pipe.SafePipeHandle))
            {
                impersonationFailure = IdentityUnavailable();
            }
            else
            {
                impersonated = true;
                if (!OpenThreadToken(GetCurrentThread(), TokenQuery, true, out SafeAccessTokenHandle tokenHandle))
                {
                    impersonationFailure = IdentityUnavailable();
                }
                else
                {
                    using (tokenHandle)
                    {
                        clientIdentity = _identityReader.Read(tokenHandle);
                    }
                }

            }
        }
        finally
        {
            if (impersonated && !_revertToSelf())
            {
                _processTerminator.TerminateAfterIdentityRestoreFailure();
                throw new InvalidOperationException("The broker process terminator returned unexpectedly.");
            }
        }

        if (impersonationFailure is not null
            || clientIdentity is null
            || clientIdentity.IsFailure)
        {
            return IdentityUnavailable();
        }

        Result<SessionIdentity> brokerIdentity = await new WindowsSessionIdentityProvider()
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        if (brokerIdentity.IsFailure)
        {
            return IdentityUnavailable();
        }

        SessionIdentity client = clientIdentity.Value;
        SessionIdentity broker = brokerIdentity.Value;
        if (!string.Equals(client.AccountSid, broker.AccountSid, StringComparison.Ordinal))
        {
            return Failure(
                BrokerErrorCodes.FSL_E_ACCOUNT_SID_MISMATCH,
                "The elevated broker account does not match the requesting account.",
                null);
        }

        if (!string.Equals(client.LogonSid, broker.LogonSid, StringComparison.Ordinal))
        {
            return Failure(
                BrokerErrorCodes.FSL_E_LOGON_SID_MISMATCH,
                "The broker and client do not belong to the same Windows logon session.",
                null);
        }

        if (client.WindowsSessionId < 0
            || broker.WindowsSessionId < 0
            || checked((uint)client.WindowsSessionId) != hello.ClientSessionId
            || checked((uint)broker.WindowsSessionId) != hello.ClientSessionId)
        {
            return SessionMismatch();
        }

        return BrokerAuthenticationResult.Success(new BrokerAuthenticatedClient(
            actualProcessId,
            processStartUtc,
            client,
            broker));
    }

    private static BrokerAuthenticationResult IdentityUnavailable() => Failure(
        BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE,
        "The client identity could not be verified.",
        null);

    private static BrokerAuthenticationResult SessionMismatch() => Failure(
        BrokerErrorCodes.FSL_E_SESSION_MISMATCH,
        "The broker and client do not belong to the same Windows session.",
        "clientSessionId");

    private static BrokerAuthenticationResult Failure(string code, string message, string? field) =>
        BrokerAuthenticationResult.Failure(new BrokerError(code, message, false, field));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(
        nint threadHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool openAsSelf,
        out SafeAccessTokenHandle tokenHandle);
}
