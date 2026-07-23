using System.Runtime.InteropServices;
using System.Security.Principal;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.App.BrokerClient;

internal interface IInitiatingClientIdentityProvider
{
    Result<InitiatingClientIdentity> Capture();
}

internal interface IInitiatingClientIdentityPlatform
{
    Result<uint> GetCurrentProcessId();

    Result<ulong> GetCurrentProcessCreationFileTime();

    Result<InitiatingTokenIdentity> ReadCurrentProcessToken();
}

internal sealed record InitiatingTokenIdentity(
    string AccountSid,
    string LogonSid,
    uint WindowsSessionId);

internal sealed class WindowsInitiatingClientIdentityProvider
    : IInitiatingClientIdentityProvider
{
    private readonly IInitiatingClientIdentityPlatform _platform;

    internal WindowsInitiatingClientIdentityProvider()
        : this(new WindowsInitiatingClientIdentityPlatform())
    {
    }

    internal WindowsInitiatingClientIdentityProvider(
        IInitiatingClientIdentityPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public Result<InitiatingClientIdentity> Capture()
    {
        Result<uint> processId = _platform.GetCurrentProcessId();
        Result<ulong> creationTime = _platform.GetCurrentProcessCreationFileTime();
        Result<InitiatingTokenIdentity> token = _platform.ReadCurrentProcessToken();
        return processId.IsFailure
            || creationTime.IsFailure
            || token.IsFailure
            || processId.Value == 0
            || creationTime.Value == 0
                ? Failure()
                : Result<InitiatingClientIdentity>.Success(new(
                    processId.Value,
                    creationTime.Value,
                    token.Value.AccountSid,
                    token.Value.LogonSid,
                    token.Value.WindowsSessionId));
    }

    private static Result<InitiatingClientIdentity> Failure() =>
        Result<InitiatingClientIdentity>.Failure(new Error(
            BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE,
            "The client identity could not be verified.",
            ErrorCategory.UnrecoverableError));
}

internal sealed class WindowsInitiatingClientIdentityPlatform
    : IInitiatingClientIdentityPlatform
{
    private const uint TokenQuery = 0x0008;
    private const uint SeGroupLogonId = 0xC0000000;
    private const int ErrorInsufficientBuffer = 122;

    public Result<uint> GetCurrentProcessId()
    {
        uint processId = GetCurrentProcessIdNative();
        return processId == 0 ? Failure<uint>() : Result<uint>.Success(processId);
    }

    public Result<ulong> GetCurrentProcessCreationFileTime()
    {
        if (!GetProcessTimes(
            GetCurrentProcess(),
            out FileTime creationTime,
            out _,
            out _,
            out _))
        {
            return Failure<ulong>();
        }

        ulong value = ((ulong)creationTime.HighDateTime << 32) | creationTime.LowDateTime;
        return value == 0 ? Failure<ulong>() : Result<ulong>.Success(value);
    }

    public Result<InitiatingTokenIdentity> ReadCurrentProcessToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out SafeAccessTokenHandle token))
        {
            return Failure<InitiatingTokenIdentity>();
        }

        using (token)
        {
            Result<string> account = ReadTokenInformation(
                token,
                TokenInformationClass.TokenUser,
                ReadAccountSid);
            Result<string> logon = ReadTokenInformation(
                token,
                TokenInformationClass.TokenGroups,
                ReadLogonSid);
            Result<uint> session = ReadTokenInformation(
                token,
                TokenInformationClass.TokenSessionId,
                ReadSessionId);
            return account.IsFailure || logon.IsFailure || session.IsFailure
                ? Failure<InitiatingTokenIdentity>()
                : Result<InitiatingTokenIdentity>.Success(new(
                    account.Value,
                    logon.Value,
                    session.Value));
        }
    }

    internal static Result<string> SelectUniqueLogonSid(
        IReadOnlyList<InitiatingTokenGroup> groups)
    {
        string? selected = null;
        foreach (InitiatingTokenGroup group in groups)
        {
            if ((group.Attributes & SeGroupLogonId) != SeGroupLogonId)
            {
                continue;
            }

            if (selected is not null)
            {
                return Failure<string>();
            }

            selected = group.Sid;
        }

        return selected is null
            ? Failure<string>()
            : Result<string>.Success(selected);
    }

    private static Result<T> ReadTokenInformation<T>(
        SafeAccessTokenHandle token,
        TokenInformationClass informationClass,
        Func<nint, uint, Result<T>> reader)
    {
        _ = GetTokenInformation(token, informationClass, nint.Zero, 0, out uint length);
        if (length == 0 || Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
        {
            return Failure<T>();
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            return GetTokenInformation(token, informationClass, buffer, length, out uint returned)
                && returned is > 0
                && returned <= length
                    ? reader(buffer, returned)
                    : Failure<T>();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Result<string> ReadAccountSid(nint buffer, uint length)
    {
        if (length < Marshal.SizeOf<SidAndAttributes>())
        {
            return Failure<string>();
        }

        return ReadSid(Marshal.PtrToStructure<SidAndAttributes>(buffer).Sid);
    }

    private static Result<string> ReadLogonSid(nint buffer, uint length)
    {
        int firstGroupOffset = Marshal.OffsetOf<TokenGroupsHeader>(
            nameof(TokenGroupsHeader.FirstGroup)).ToInt32();
        int groupSize = Marshal.SizeOf<SidAndAttributes>();
        if (length < firstGroupOffset)
        {
            return Failure<string>();
        }

        uint count = unchecked((uint)Marshal.ReadInt32(buffer));
        if (firstGroupOffset + ((long)count * groupSize) > length)
        {
            return Failure<string>();
        }

        var groups = new InitiatingTokenGroup[count];
        for (uint index = 0; index < count; index++)
        {
            SidAndAttributes group = Marshal.PtrToStructure<SidAndAttributes>(
                buffer + firstGroupOffset + ((int)index * groupSize));
            Result<string> sid = ReadSid(group.Sid);
            if (sid.IsFailure)
            {
                return Failure<string>();
            }

            groups[index] = new InitiatingTokenGroup(sid.Value, group.Attributes);
        }

        return SelectUniqueLogonSid(groups);
    }

    private static Result<uint> ReadSessionId(nint buffer, uint length) =>
        length < sizeof(uint)
            ? Failure<uint>()
            : Result<uint>.Success(unchecked((uint)Marshal.ReadInt32(buffer)));

    private static Result<string> ReadSid(nint sid) =>
        sid == nint.Zero || !IsValidSid(sid)
            ? Failure<string>()
            : Result<string>.Success(new SecurityIdentifier(sid).Value);

    private static Result<T> Failure<T>() => Result<T>.Failure(new Error(
        BrokerErrorCodes.FSL_E_CLIENT_IDENTITY_UNAVAILABLE,
        "The client identity could not be verified.",
        ErrorCategory.UnrecoverableError));

    internal readonly record struct InitiatingTokenGroup(string Sid, uint Attributes);

    private enum TokenInformationClass
    {
        TokenUser = 1,
        TokenGroups = 2,
        TokenSessionId = 12,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal nint Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsHeader
    {
        internal uint GroupCount;
        internal SidAndAttributes FirstGroup;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")]
    private static extern uint GetCurrentProcessIdNative();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        nint process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        TokenInformationClass informationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(nint sid);
}
