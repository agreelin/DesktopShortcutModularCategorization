using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.App.BrokerClient;

internal interface IRecoveryReadinessSnapshotPlatform
{
    Result<byte[]> Read();
}

internal sealed class WindowsRecoveryReadinessReader : IRecoveryReadinessReader
{
    private readonly IRecoveryReadinessSnapshotPlatform _platform;

    internal WindowsRecoveryReadinessReader()
        : this(new WindowsRecoveryReadinessSnapshotPlatform())
    {
    }

    internal WindowsRecoveryReadinessReader(IRecoveryReadinessSnapshotPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public ValueTask<RecoveryReadinessSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<byte[]> read = _platform.Read();
        if (read.IsFailure)
        {
            return ValueTask.FromException<RecoveryReadinessSnapshot>(
                new RecoveryReadinessException(read.Error!.Code));
        }

        Result<RecoveryReadinessSnapshot> parsed = RecoveryReadinessJson.Deserialize(read.Value);
        return parsed.IsFailure
            ? ValueTask.FromException<RecoveryReadinessSnapshot>(
                new RecoveryReadinessException(parsed.Error!.Code))
            : ValueTask.FromResult(parsed.Value);
    }
}

internal sealed class WindowsRecoveryReadinessSnapshotPlatform
    : IRecoveryReadinessSnapshotPlatform
{
    private static readonly Guid ProgramData =
        new("62ab5d82-fdc1-4dc3-a9dd-070d1d495d97");
    private const uint GenericRead = 0x80000000;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileOpen = 1;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonalert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdInfo = 18;
    private const int FileStandardInfo = 1;
    private const int FullControl = 0x001F01FF;
    private const int DirectoryUsersRead = 0x001200A9;
    private const int FileUsersRead = 0x00120089;
    private const string RecoveryServiceAccount = @"NT SERVICE\FolderSessionLockRecovery";

    public Result<byte[]> Read()
    {
        Result<string> programData = GetKnownFolder();
        if (programData.IsFailure)
        {
            return Failure<byte[]>(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_OPEN_FAILED);
        }

        string directoryPath = Path.Combine(
            programData.Value,
            "FolderSessionLock",
            "Readiness");
        using SafeFileHandle directory = CreateFile(
            directoryPath,
            GenericRead | ReadControl,
            FileShareRead | FileShareWrite | FileShareDelete,
            nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            nint.Zero);
        if (directory.IsInvalid
            || !ReadIdentity(directory, out FileIdentity directoryIdentity)
            || !VerifyAttributes(directory, requireDirectory: true)
            || !VerifySecurity(directory, DirectoryUsersRead))
        {
            return Failure<byte[]>(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID);
        }

        Result<SafeFileHandle> open = OpenRelative(
            directory,
            RecoveryReadinessPolicy.CanonicalLeafName);
        if (open.IsFailure)
        {
            return Result<byte[]>.Failure(open.Error!);
        }

        using SafeFileHandle file = open.Value;
        if (!ReadIdentity(file, out FileIdentity beforeIdentity)
            || !VerifyAttributes(file, requireDirectory: false)
            || !GetFileInformationByHandleEx(
                file,
                FileStandardInfo,
                out FileStandardInformation standard,
                (uint)Marshal.SizeOf<FileStandardInformation>())
            || standard.NumberOfLinks != 1
            || standard.EndOfFile is < 1 or > RecoveryReadinessPolicy.MaximumLength
            || !VerifySecurity(file, FileUsersRead))
        {
            return Failure<byte[]>(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID);
        }

        byte[] bytes;
        try
        {
            bytes = new byte[checked((int)standard.EndOfFile)];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = RandomAccess.Read(file, bytes.AsSpan(read), read);
                if (count == 0)
                {
                    return Failure<byte[]>(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID);
                }

                read += count;
            }
        }
        catch (IOException)
        {
            return Failure<byte[]>(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_OPEN_FAILED);
        }

        return ReadIdentity(file, out FileIdentity afterIdentity)
            && beforeIdentity == afterIdentity
            && VerifySecurity(file, FileUsersRead)
            && ReadIdentity(directory, out FileIdentity finalDirectoryIdentity)
            && directoryIdentity == finalDirectoryIdentity
                ? Result<byte[]>.Success(bytes)
                : Failure<byte[]>(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED);
    }

    private static Result<string> GetKnownFolder()
    {
        int result = SHGetKnownFolderPath(in ProgramData, 0, nint.Zero, out nint pointer);
        if (result < 0)
        {
            return Failure<string>(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_OPEN_FAILED);
        }

        try
        {
            string? path = Marshal.PtrToStringUni(pointer);
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                ? Result<string>.Success(Path.GetFullPath(path))
                : Failure<string>(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_OPEN_FAILED);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static Result<SafeFileHandle> OpenRelative(
        SafeFileHandle directory,
        string leafName)
    {
        nint nameBuffer = Marshal.StringToHGlobalUni(leafName);
        try
        {
            var name = new UnicodeString
            {
                Length = checked((ushort)(leafName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((leafName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer,
            };
            nint nameStructure = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(name, nameStructure, false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = directory.DangerousGetHandle(),
                    ObjectName = nameStructure,
                    Attributes = 0x00000040,
                };
                int status = NtCreateFile(
                    out nint raw,
                    GenericRead | ReadControl | Synchronize,
                    ref attributes,
                    out _,
                    nint.Zero,
                    0,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileOpen,
                    FileNonDirectoryFile | FileSynchronousIoNonalert | FileOpenReparsePoint,
                    nint.Zero,
                    0);
                return status >= 0
                    ? Result<SafeFileHandle>.Success(new SafeFileHandle(raw, ownsHandle: true))
                    : Failure<SafeFileHandle>(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_NOT_FOUND);
            }
            finally
            {
                Marshal.FreeHGlobal(nameStructure);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuffer);
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

    private static bool ReadIdentity(SafeFileHandle handle, out FileIdentity identity)
    {
        identity = default;
        if (!GetFileInformationByHandleEx(
            handle,
            FileIdInfo,
            out FileIdInformation information,
            (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            return false;
        }

        unsafe
        {
            byte* id = information.FileId.Identifier;
            identity = new FileIdentity(
                information.VolumeSerialNumber,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    new ReadOnlySpan<byte>(id + 8, 8)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    new ReadOnlySpan<byte>(id, 8)));
        }

        return true;
    }

    private static bool VerifySecurity(SafeFileHandle handle, int usersMask)
    {
        if (!ResolveServiceSid(out string serviceSid))
        {
            return false;
        }

        uint result = GetSecurityInfo(
            handle,
            1,
            OwnerSecurityInformation | DaclSecurityInformation,
            out nint owner,
            out _,
            out nint dacl,
            out _,
            out nint descriptor);
        if (result != 0 || owner == nint.Zero || dacl == nint.Zero || descriptor == nint.Zero)
        {
            if (descriptor != nint.Zero)
            {
                LocalFree(descriptor);
            }

            return false;
        }

        try
        {
            if (!GetSecurityDescriptorControl(descriptor, out ushort control, out _)
                || ((ControlFlags)control & ControlFlags.DiscretionaryAclProtected) == 0
                || !GetAclInformation(
                    dacl,
                    out AclSizeInformation size,
                    (uint)Marshal.SizeOf<AclSizeInformation>(),
                    2)
                || size.AceCount != 4)
            {
                return false;
            }

            string[] sids =
            [
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                serviceSid,
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value,
            ];
            if (new SecurityIdentifier(owner).Value != sids[0])
            {
                return false;
            }

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
                    || ace.AceType != AceType.AccessAllowed
                    || ace.AceQualifier != AceQualifier.AccessAllowed
                    || ace.AceFlags != AceFlags.None
                    || ace.IsCallback
                    || ace is ObjectAce
                    || ace.AccessMask != (index == 3 ? usersMask : FullControl)
                    || ace.SecurityIdentifier.Value != sids[index])
                {
                    return false;
                }
            }

            return true;
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

    private static bool ResolveServiceSid(out string sid)
    {
        sid = string.Empty;
        uint sidLength = 0;
        uint domainLength = 0;
        _ = LookupAccountName(
            null,
            RecoveryServiceAccount,
            nint.Zero,
            ref sidLength,
            null,
            ref domainLength,
            out _);
        if (sidLength == 0)
        {
            return false;
        }

        nint sidBuffer = Marshal.AllocHGlobal(checked((int)sidLength));
        var domain = new char[domainLength];
        try
        {
            if (!LookupAccountName(
                null,
                RecoveryServiceAccount,
                sidBuffer,
                ref sidLength,
                domain,
                ref domainLength,
                out _))
            {
                return false;
            }

            sid = new SecurityIdentifier(sidBuffer).Value;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(sidBuffer);
        }
    }

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(new Error(
        code,
        code,
        ErrorCategory.UnrecoverableError));

    private readonly record struct FileIdentity(
        ulong VolumeSerialNumber,
        ulong FileIdHigh,
        ulong FileIdLow);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal int Length;
        internal nint RootDirectory;
        internal nint ObjectName;
        internal uint Attributes;
        internal nint SecurityDescriptor;
        internal nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal nint Status;
        internal nuint Information;
    }

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
    private struct FileStandardInformation
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
        internal ushort Padding;
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

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out nint fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        nint allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        nint extendedAttributes,
        uint extendedAttributesLength);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileStandardInformation fileInformation,
        uint bufferSize);

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
    private static extern bool GetSecurityDescriptorControl(
        nint securityDescriptor,
        out ushort control,
        out uint revision);

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

    [DllImport("advapi32.dll", EntryPoint = "LookupAccountNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountName(
        string? systemName,
        string accountName,
        nint sid,
        ref uint sidSize,
        char[]? referencedDomainName,
        ref uint referencedDomainNameSize,
        out int use);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
