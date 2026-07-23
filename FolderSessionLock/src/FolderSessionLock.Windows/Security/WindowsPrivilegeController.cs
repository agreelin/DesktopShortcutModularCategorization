using System.Runtime.InteropServices;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Security;

internal interface IWindowsPrivilegeController
{
    Result<IWindowsPrivilegeLease> EnableRestorePrivilege();
}

internal interface IWindowsPrivilegeLease : IDisposable
{
    Result Revert();
}

internal sealed class WindowsPrivilegeController : IWindowsPrivilegeController
{
    internal const string RestorePrivilegeName = "SeRestorePrivilege";

    public Result<IWindowsPrivilegeLease> EnableRestorePrivilege()
    {
        if (NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenQuery | NativeMethods.TokenAdjustPrivileges,
                out SafeAccessTokenHandle token) == 0)
        {
            return EnableFailure();
        }

        if (NativeMethods.LookupPrivilegeValue(
                null,
                RestorePrivilegeName,
                out NativeMethods.Luid luid) == 0)
        {
            token.Dispose();
            return EnableFailure();
        }

        var enabled = new NativeMethods.TokenPrivileges(
            1,
            new NativeMethods.LuidAndAttributes(luid, NativeMethods.SePrivilegeEnabled));
        if (NativeMethods.AdjustTokenPrivileges(
                token,
                false,
                ref enabled,
                (uint)Marshal.SizeOf<NativeMethods.TokenPrivileges>(),
                out NativeMethods.TokenPrivileges previous,
                out _) == 0
            || Marshal.GetLastPInvokeError() == NativeMethods.ErrorNotAllAssigned)
        {
            token.Dispose();
            return EnableFailure();
        }

        return Result<IWindowsPrivilegeLease>.Success(
            new WindowsPrivilegeLease(token, luid, previous.Privilege.Attributes));
    }

    private static Result<IWindowsPrivilegeLease> EnableFailure() =>
        Result<IWindowsPrivilegeLease>.Failure(new Error(
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_OWNER_PRIVILEGE_UNAVAILABLE,
            BrokerErrorCodes.FSL_E_RECOVERY_FILE_OWNER_PRIVILEGE_UNAVAILABLE,
            ErrorCategory.UnrecoverableError));

    private sealed class WindowsPrivilegeLease(
        SafeAccessTokenHandle token,
        NativeMethods.Luid luid,
        uint previousAttributes) : IWindowsPrivilegeLease
    {
        private int _reverted;

        public Result Revert()
        {
            if (Interlocked.Exchange(ref _reverted, 1) != 0)
            {
                return Result.Success();
            }

            var previous = new NativeMethods.TokenPrivileges(
                1,
                new NativeMethods.LuidAndAttributes(luid, previousAttributes));
            return NativeMethods.AdjustTokenPrivileges(
                    token,
                    false,
                    ref previous,
                    0,
                    out _,
                    out _) != 0
                && Marshal.GetLastPInvokeError() != NativeMethods.ErrorNotAllAssigned
                    ? Result.Success()
                    : Result.Failure(new Error(
                        BrokerErrorCodes.FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED,
                        BrokerErrorCodes.FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED,
                        ErrorCategory.UnrecoverableError));
        }

        public void Dispose() => token.Dispose();
    }
}
