using System.Runtime.InteropServices;
using System.Security.Principal;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Security;

public sealed class WindowsAccessTokenIdentityReader
{
    private static readonly Error InvalidTokenInformationError = new(
        "windows.session_identity.invalid_token_information",
        "Windows returned invalid access token information.",
        ErrorCategory.PlatformError);

    private static readonly Error InvalidSidError = new(
        "windows.session_identity.invalid_sid",
        "Windows returned an invalid security identifier.",
        ErrorCategory.PlatformError);

    private static readonly Error LogonSidNotFoundError = new(
        "windows.session_identity.logon_sid_not_found",
        "The current access token does not contain a Logon SID.",
        ErrorCategory.PlatformError);

    private static readonly Error LogonSidNotUniqueError = new(
        "windows.session_identity.logon_sid_not_unique",
        "The current access token contains more than one Logon SID.",
        ErrorCategory.PlatformError);

    public Result<SessionIdentity> Read(SafeAccessTokenHandle tokenHandle)
    {
        ArgumentNullException.ThrowIfNull(tokenHandle);
        if (tokenHandle.IsInvalid || tokenHandle.IsClosed)
        {
            return Result<SessionIdentity>.Failure(InvalidTokenInformationError);
        }

        Result<string> accountSid = ReadTokenInformation(
            tokenHandle,
            NativeMethods.TokenInformationClass.TokenUser,
            ReadAccountSid);
        if (accountSid.IsFailure)
        {
            return Result<SessionIdentity>.Failure(accountSid.Error!);
        }

        Result<string> logonSid = ReadTokenInformation(
            tokenHandle,
            NativeMethods.TokenInformationClass.TokenGroups,
            ReadLogonSid);
        if (logonSid.IsFailure)
        {
            return Result<SessionIdentity>.Failure(logonSid.Error!);
        }

        Result<int> sessionId = ReadTokenInformation(
            tokenHandle,
            NativeMethods.TokenInformationClass.TokenSessionId,
            ReadWindowsSessionId);
        return sessionId.IsSuccess
            ? Result<SessionIdentity>.Success(new SessionIdentity(
                accountSid.Value,
                logonSid.Value,
                sessionId.Value))
            : Result<SessionIdentity>.Failure(sessionId.Error!);
    }

    internal static Result<string> SelectUniqueLogonSid(IReadOnlyList<TokenGroupIdentity> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        string? logonSid = null;
        foreach (TokenGroupIdentity group in groups)
        {
            if ((group.Attributes & NativeMethods.SeGroupLogonId) != NativeMethods.SeGroupLogonId)
            {
                continue;
            }

            if (logonSid is not null)
            {
                return Result<string>.Failure(LogonSidNotUniqueError);
            }

            logonSid = group.Sid;
        }

        return logonSid is null
            ? Result<string>.Failure(LogonSidNotFoundError)
            : Result<string>.Success(logonSid);
    }

    private static Result<T> ReadTokenInformation<T>(
        SafeAccessTokenHandle tokenHandle,
        NativeMethods.TokenInformationClass informationClass,
        Func<nint, uint, Result<T>> reader)
    {
        int queryResult = NativeMethods.GetTokenInformation(
            tokenHandle,
            informationClass,
            nint.Zero,
            0,
            out uint requiredLength);
        if (queryResult == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != NativeMethods.ErrorInsufficientBuffer)
            {
                return NativeFailure<T>(nameof(NativeMethods.GetTokenInformation), error);
            }
        }

        if (requiredLength == 0 || requiredLength > int.MaxValue)
        {
            return Result<T>.Failure(InvalidTokenInformationError);
        }

        nint buffer = Marshal.AllocHGlobal((int)requiredLength);
        try
        {
            if (NativeMethods.GetTokenInformation(
                    tokenHandle,
                    informationClass,
                    buffer,
                    requiredLength,
                    out uint returnedLength) == 0)
            {
                return NativeFailure<T>(
                    nameof(NativeMethods.GetTokenInformation),
                    Marshal.GetLastPInvokeError());
            }

            return returnedLength == 0 || returnedLength > requiredLength
                ? Result<T>.Failure(InvalidTokenInformationError)
                : reader(buffer, returnedLength);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Result<string> ReadAccountSid(nint buffer, uint bufferLength)
    {
        if (bufferLength < Marshal.SizeOf<NativeMethods.SidAndAttributes>())
        {
            return Result<string>.Failure(InvalidTokenInformationError);
        }

        NativeMethods.SidAndAttributes user =
            Marshal.PtrToStructure<NativeMethods.SidAndAttributes>(buffer);
        return ReadSid(user.Sid);
    }

    private static Result<string> ReadLogonSid(nint buffer, uint bufferLength)
    {
        int firstGroupOffset = Marshal.OffsetOf<NativeMethods.TokenGroupsHeader>(
            nameof(NativeMethods.TokenGroupsHeader.FirstGroup)).ToInt32();
        int groupSize = Marshal.SizeOf<NativeMethods.SidAndAttributes>();
        if (bufferLength < firstGroupOffset)
        {
            return Result<string>.Failure(InvalidTokenInformationError);
        }

        uint groupCount = unchecked((uint)Marshal.ReadInt32(buffer));
        long requiredLength = firstGroupOffset + ((long)groupCount * groupSize);
        if (requiredLength > bufferLength)
        {
            return Result<string>.Failure(InvalidTokenInformationError);
        }

        var groups = new TokenGroupIdentity[groupCount];
        for (uint index = 0; index < groupCount; index++)
        {
            nint address = buffer + firstGroupOffset + ((int)index * groupSize);
            NativeMethods.SidAndAttributes group =
                Marshal.PtrToStructure<NativeMethods.SidAndAttributes>(address);
            Result<string> sid = ReadSid(group.Sid);
            if (sid.IsFailure)
            {
                return Result<string>.Failure(sid.Error!);
            }

            groups[index] = new TokenGroupIdentity(sid.Value, group.Attributes);
        }

        return SelectUniqueLogonSid(groups);
    }

    private static Result<int> ReadWindowsSessionId(nint buffer, uint bufferLength)
    {
        if (bufferLength < sizeof(uint))
        {
            return Result<int>.Failure(InvalidTokenInformationError);
        }

        uint sessionId = unchecked((uint)Marshal.ReadInt32(buffer));
        return sessionId <= int.MaxValue
            ? Result<int>.Success((int)sessionId)
            : Result<int>.Failure(InvalidTokenInformationError);
    }

    private static Result<string> ReadSid(nint sid) =>
        sid == nint.Zero || NativeMethods.IsValidSid(sid) == 0
            ? Result<string>.Failure(InvalidSidError)
            : Result<string>.Success(new SecurityIdentifier(sid).Value);

    private static Result<T> NativeFailure<T>(string operation, int nativeError) =>
        Result<T>.Failure(new Error(
            "windows.session_identity.native_call_failed",
            $"{operation} failed with Windows error {nativeError}.",
            ErrorCategory.PlatformError));

    internal readonly record struct TokenGroupIdentity(string Sid, uint Attributes);
}
