using System.Runtime.InteropServices;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Services;

public sealed class WindowsSessionIdentityProvider : ISessionIdentityProvider
{
    private readonly WindowsAccessTokenIdentityReader _identityReader = new();

    public ValueTask<Result<SessionIdentity>> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenQuery,
                out SafeAccessTokenHandle tokenHandle) == 0)
        {
            return ValueTask.FromResult(Result<SessionIdentity>.Failure(new Error(
                "windows.session_identity.native_call_failed",
                $"{nameof(NativeMethods.OpenProcessToken)} failed with Windows error {Marshal.GetLastPInvokeError()}.",
                ErrorCategory.PlatformError)));
        }

        using (tokenHandle)
        {
            return ValueTask.FromResult(_identityReader.Read(tokenHandle));
        }
    }

    internal static Result<string> SelectUniqueLogonSid(IReadOnlyList<TokenGroupIdentity> groups) =>
        WindowsAccessTokenIdentityReader.SelectUniqueLogonSid(groups
            .Select(group => new WindowsAccessTokenIdentityReader.TokenGroupIdentity(
                group.Sid,
                group.Attributes))
            .ToArray());

    internal readonly record struct TokenGroupIdentity(string Sid, uint Attributes);
}
