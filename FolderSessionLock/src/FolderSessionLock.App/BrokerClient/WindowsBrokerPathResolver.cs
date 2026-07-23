using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.App.BrokerClient;

internal sealed record BrokerFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdHigh,
    ulong FileIdLow);

internal sealed record ResolvedBrokerPath(
    string InstallationDirectory,
    string BrokerPath,
    BrokerFileIdentity Identity);

internal interface IBrokerPathResolver
{
    Result<ResolvedBrokerPath> Resolve();
}

internal interface IBrokerPathPlatform
{
    Result<string> GetProgramFilesPath();

    Result<BrokerFileIdentity> Verify(
        string installationDirectory,
        string brokerPath);
}

internal sealed class WindowsBrokerPathResolver : IBrokerPathResolver
{
    internal const string InstallationDirectoryName = "FolderSessionLock";
    internal const string BrokerFileName = "FolderSessionLock.Broker.exe";
    private readonly IBrokerPathPlatform _platform;

    internal WindowsBrokerPathResolver()
        : this(new WindowsBrokerPathPlatform())
    {
    }

    internal WindowsBrokerPathResolver(IBrokerPathPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public Result<ResolvedBrokerPath> Resolve()
    {
        Result<string> programFiles = _platform.GetProgramFilesPath();
        if (programFiles.IsFailure || !Path.IsPathFullyQualified(programFiles.Value))
        {
            return Failure();
        }

        string installationDirectory = Path.Combine(
            Path.GetFullPath(programFiles.Value),
            InstallationDirectoryName);
        string brokerPath = Path.Combine(installationDirectory, BrokerFileName);
        Result<BrokerFileIdentity> verification = _platform.Verify(
            installationDirectory,
            brokerPath);
        return verification.IsFailure
            ? Failure()
            : Result<ResolvedBrokerPath>.Success(new(
                installationDirectory,
                brokerPath,
                verification.Value));
    }

    private static Result<ResolvedBrokerPath> Failure() =>
        Result<ResolvedBrokerPath>.Failure(new Error(
            BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED,
            "The elevated broker installation could not be verified.",
            ErrorCategory.UnrecoverableError));
}

internal sealed class WindowsBrokerPathPlatform : IBrokerPathPlatform
{
    private static readonly Guid ProgramFiles =
        new("905e63b6-c1bf-494e-b29c-65b732d3d21a");
    private const uint FileReadData = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint ReadControl = 0x00020000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdInfo = 18;
    private const int FullControl = 0x001F01FF;
    private const int ReadAndExecute = 0x001200A9;
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    public Result<string> GetProgramFilesPath()
    {
        int result = SHGetKnownFolderPath(in ProgramFiles, 0, nint.Zero, out nint pointer);
        if (result < 0)
        {
            return Failure<string>();
        }

        try
        {
            string? path = Marshal.PtrToStringUni(pointer);
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                ? Result<string>.Success(Path.GetFullPath(path))
                : Failure<string>();
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    public Result<BrokerFileIdentity> Verify(
        string installationDirectory,
        string brokerPath)
    {
        using SafeFileHandle directory = CreateFile(
            installationDirectory,
            FileReadAttributes | ReadControl,
            FileShareRead | FileShareWrite | FileShareDelete,
            nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            nint.Zero);
        if (directory.IsInvalid
            || !VerifyAttributes(directory, requireDirectory: true)
            || !VerifyFinalPath(directory, installationDirectory)
            || !VerifyInstallDirectorySecurity(directory))
        {
            return Failure<BrokerFileIdentity>();
        }

        using SafeFileHandle file = CreateFile(
            brokerPath,
            FileReadData | FileReadAttributes,
            FileShareRead,
            nint.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            nint.Zero);
        if (file.IsInvalid
            || !VerifyAttributes(file, requireDirectory: false)
            || !VerifyFinalPath(file, brokerPath)
            || !string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(brokerPath)),
                Path.GetFullPath(installationDirectory),
                StringComparison.OrdinalIgnoreCase)
            || !GetFileInformationByHandleEx(
                file,
                FileIdInfo,
                out FileIdInformation identity,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            return Failure<BrokerFileIdentity>();
        }

        unsafe
        {
            byte* id = identity.FileId.Identifier;
            return Result<BrokerFileIdentity>.Success(new(
                identity.VolumeSerialNumber,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    new ReadOnlySpan<byte>(id + 8, 8)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    new ReadOnlySpan<byte>(id, 8))));
        }
    }

    private static bool VerifyAttributes(SafeFileHandle handle, bool requireDirectory) =>
        GetFileInformationByHandleEx(
            handle,
            FileAttributeTagInfo,
            out FileAttributeTagInformation attributes,
            (uint)Marshal.SizeOf<FileAttributeTagInformation>())
        && (attributes.FileAttributes & FileAttributeReparsePoint) == 0
        && ((attributes.FileAttributes & FileAttributeDirectory) != 0) == requireDirectory;

    private static unsafe bool VerifyFinalPath(SafeFileHandle handle, string expectedPath)
    {
        var buffer = new char[32768];
        fixed (char* pointer = buffer)
        {
            uint length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length)
            {
                return false;
            }

            string finalPath = NormalizeFinalPath(new string(pointer, 0, (int)length));
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                finalPath,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool VerifyInstallDirectorySecurity(SafeFileHandle handle)
    {
        uint result = GetSecurityInfo(
            handle,
            1,
            OwnerSecurityInformation | DaclSecurityInformation,
            out nint owner,
            out _,
            out nint dacl,
            out _,
            out nint descriptor);
        if (result != 0 || descriptor == nint.Zero || owner == nint.Zero || dacl == nint.Zero)
        {
            if (descriptor != nint.Zero)
            {
                LocalFree(descriptor);
            }

            return false;
        }

        try
        {
            string ownerSid = new SecurityIdentifier(owner).Value;
            string systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
            string administratorsSid = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null).Value;
            string usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;
            if (ownerSid != systemSid && ownerSid != TrustedInstallerSid)
            {
                return false;
            }

            if (!GetAclInformation(
                dacl,
                out AclSizeInformation size,
                (uint)Marshal.SizeOf<AclSizeInformation>(),
                2))
            {
                return false;
            }

            var aces = new List<QualifiedAce>();
            for (uint index = 0; index < size.AceCount; index++)
            {
                if (!GetAce(dacl, index, out nint acePointer))
                {
                    return false;
                }

                AceHeader header = Marshal.PtrToStructure<AceHeader>(acePointer);
                var bytes = new byte[header.AceSize];
                Marshal.Copy(acePointer, bytes, 0, bytes.Length);
                if (GenericAce.CreateFromBinaryForm(bytes, 0) is not QualifiedAce ace
                    || ace.AceQualifier != AceQualifier.AccessAllowed
                    || ace.IsCallback
                    || ace is ObjectAce)
                {
                    return false;
                }

                aces.Add(ace);
            }

            return HasRequired(aces, systemSid, FullControl)
                && HasRequired(aces, administratorsSid, FullControl)
                && HasRequired(aces, usersSid, ReadAndExecute)
                && aces.Where(ace => ace.SecurityIdentifier.Value is var sid
                        && (sid == usersSid
                            || sid == new SecurityIdentifier(
                                WellKnownSidType.AuthenticatedUserSid,
                                null).Value
                            || sid == new SecurityIdentifier(
                                WellKnownSidType.WorldSid,
                                null).Value))
                    .All(ace => (ace.AccessMask & 0x000D0156) == 0);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return false;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static bool HasRequired(
        IEnumerable<QualifiedAce> aces,
        string sid,
        int mask) => aces.Any(ace =>
            ace.SecurityIdentifier.Value == sid
            && ace.AccessMask == mask
            && ace.AceFlags == (AceFlags.ContainerInherit | AceFlags.ObjectInherit));

    private static string NormalizeFinalPath(string path)
    {
        const string prefix = "\\\\?\\";
        const string uncPrefix = "\\\\?\\UNC\\";
        string dosPath = path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"\\\\{path[uncPrefix.Length..]}"
            : path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? path[prefix.Length..]
                : path;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dosPath));
    }

    private static Result<T> Failure<T>() => Result<T>.Failure(new Error(
        BrokerErrorCodes.FSL_E_BROKER_PATH_UNTRUSTED,
        "The elevated broker installation could not be verified.",
        ErrorCategory.UnrecoverableError));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FileId128
    {
        internal fixed byte Identifier[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AclSizeInformation
    {
        internal uint AceCount;
        internal uint AclBytesInUse;
        internal uint AclBytesFree;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AceHeader
    {
        internal byte AceType;
        internal byte AceFlags;
        internal ushort AceSize;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        in Guid folderId,
        uint flags,
        nint token,
        out nint path);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        char* filePath,
        uint filePathSize,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAclInformation(
        nint acl,
        out AclSizeInformation information,
        uint informationLength,
        int informationClass);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAce(nint acl, uint aceIndex, out nint ace);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
