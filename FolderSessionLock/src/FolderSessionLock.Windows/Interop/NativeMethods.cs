using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Interop;

internal static partial class NativeMethods
{
    internal const uint ReadControl = 0x00020000;
    internal const uint WriteDac = 0x00040000;
    internal const uint FileReadData = 0x00000001;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint FileAttributeDirectory = 0x00000010;
    internal const uint FileAttributeReparsePoint = 0x00000400;
    internal const uint DriveRemovable = 2;
    internal const uint DriveFixed = 3;
    internal const uint DriveRemote = 4;
    internal const uint DriveCdRom = 5;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const uint TokenQuery = 0x00000008;
    internal const uint TokenAdjustPrivileges = 0x00000020;
    internal const uint SePrivilegeEnabled = 0x00000002;
    internal const uint SeGroupLogonId = 0xC0000000;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorNotAllAssigned = 1300;
    internal const uint ErrorSuccess = 0;
    internal const uint OwnerSecurityInformation = 0x00000001;
    internal const uint GroupSecurityInformation = 0x00000002;
    internal const uint DaclSecurityInformation = 0x00000004;
    internal const uint UnprotectedDaclSecurityInformation = 0x20000000;
    internal const uint ProtectedDaclSecurityInformation = 0x80000000;

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "LookupPrivilegeValueW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int LookupPrivilegeValue(
        string? systemName,
        string name,
        out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        out TokenPrivileges previousState,
        out uint returnLength);

    [LibraryImport("advapi32.dll")]
    internal static partial int IsValidSid(nint sid);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetDriveTypeW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveType(string rootPathName);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    internal static partial int GetFileAttributeTagInfo(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass informationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    internal static partial int GetFileIdInfo(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass informationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    internal static partial int GetFileStandardInfo(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass informationClass,
        out FileStandardInfo fileInformation,
        uint bufferSize);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    internal static unsafe partial uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        char* filePath,
        uint filePathLength,
        uint flags);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetVolumeInformationByHandleW",
        SetLastError = true)]
    internal static unsafe partial int GetVolumeInformationByHandle(
        SafeFileHandle fileHandle,
        nint volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        char* fileSystemNameBuffer,
        uint fileSystemNameSize);

    [LibraryImport("shell32.dll", SetLastError = false)]
    internal static partial int SHGetKnownFolderPath(
        in Guid knownFolderId,
        uint flags,
        nint token,
        out nint path);

    [LibraryImport("advapi32.dll")]
    internal static partial uint GetSecurityInfo(
        SafeFileHandle handle,
        SeObjectType objectType,
        uint securityInformation,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint securityDescriptor);

    [LibraryImport("advapi32.dll")]
    internal static partial uint SetSecurityInfo(
        SafeFileHandle handle,
        SeObjectType objectType,
        uint securityInformation,
        nint owner,
        nint group,
        nint dacl,
        nint sacl);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetSecurityDescriptorControl(
        nint securityDescriptor,
        out ushort control,
        out uint revision);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetAclInformation(
        nint acl,
        out AclSizeInformation information,
        uint informationLength,
        AclInformationClass informationClass);

    [LibraryImport("advapi32.dll")]
    internal static partial int IsValidAcl(nint acl);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetAce(nint acl, uint aceIndex, out nint ace);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);

    internal enum TokenInformationClass
    {
        TokenUser = 1,
        TokenGroups = 2,
        TokenSessionId = 12,
    }

    internal enum FileInfoByHandleClass
    {
        FileStandardInfo = 1,
        FileAttributeTagInfo = 9,
        FileIdInfo = 18,
    }

    internal enum SeObjectType
    {
        FileObject = 1,
    }

    internal enum AclInformationClass
    {
        AclSizeInformation = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        internal SidAndAttributes(nint sid, uint attributes)
        {
            Sid = sid;
            Attributes = attributes;
        }

        internal nint Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenGroupsHeader
    {
        internal TokenGroupsHeader(uint groupCount, SidAndAttributes firstGroup)
        {
            GroupCount = groupCount;
            FirstGroup = firstGroup;
        }

        internal uint GroupCount;
        internal SidAndAttributes FirstGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LuidAndAttributes
    {
        internal LuidAndAttributes(Luid luid, uint attributes)
        {
            Luid = luid;
            Attributes = attributes;
        }

        internal Luid Luid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        internal TokenPrivileges(uint privilegeCount, LuidAndAttributes privilege)
        {
            PrivilegeCount = privilegeCount;
            Privilege = privilege;
        }

        internal uint PrivilegeCount;
        internal LuidAndAttributes Privilege;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct FileId128
    {
        internal fixed byte Identifier[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct FileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AclSizeInformation
    {
        internal uint AceCount;
        internal uint AclBytesInUse;
        internal uint AclBytesFree;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AclHeader
    {
        internal byte AclRevision;
        internal byte Sbz1;
        internal ushort AclSize;
        internal ushort AceCount;
        internal ushort Sbz2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AceHeader
    {
        internal byte AceType;
        internal byte AceFlags;
        internal ushort AceSize;
    }
}
