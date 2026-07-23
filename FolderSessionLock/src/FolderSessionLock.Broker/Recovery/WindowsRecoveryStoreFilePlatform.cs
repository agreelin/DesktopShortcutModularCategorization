using System.Runtime.InteropServices;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery;

internal interface IRecoveryStoreFilePlatform
{
    Result<SafeFileHandle> OpenDirectory(string path);

    Result<SafeFileHandle> CreateTemporary(
        SafeFileHandle directoryHandle,
        string leafName);

    Result<SafeFileHandle> OpenExisting(
        SafeFileHandle directoryHandle,
        string leafName);

    Result<RecoveryRecordFileIdentity> GetIdentity(SafeFileHandle handle);

    Result<NativeMethods.FileAttributeTagInfo> GetAttributes(SafeFileHandle handle);

    Result<string> GetFinalPath(SafeFileHandle handle);

    Result WriteAll(SafeFileHandle handle, ReadOnlyMemory<byte> bytes);

    Result Flush(SafeFileHandle handle);

    Result<byte[]> ReadAll(SafeFileHandle handle, int maximumLength);

    Result Rename(
        SafeFileHandle fileHandle,
        SafeFileHandle directoryHandle,
        string targetLeafName,
        bool replaceExisting);

    Result Delete(SafeFileHandle fileHandle);

    Result CloseAfterDisposition(SafeFileHandle fileHandle);

    Result<RecoveryRecordFileIdentity?> GetLeafIdentity(
        SafeFileHandle directoryHandle,
        string leafName);
}

internal interface IRecoveryStoreRenameNative
{
    int SetRenameInformation(
        SafeFileHandle fileHandle,
        nint fileInformation,
        uint length,
        int fileInformationClass);

    uint NtStatusToDosError(int status);
}

internal sealed class WindowsRecoveryStoreFilePlatform : IRecoveryStoreFilePlatform
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint WriteOwner = 0x00080000;
    private const uint FileCreate = 2;
    private const uint FileOpen = 1;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonalert = 0x00000020;
    private const uint FileWriteThrough = 0x00000002;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileRenameReplaceIfExists = 0x00000001;
    private const uint FileRenamePosixSemantics = 0x00000002;
    private const uint FileDispositionDelete = 0x00000001;
    private const uint FileDispositionPosixSemantics = 0x00000002;
    private const int FileDispositionInfoEx = 21;
    private const int FileRenameInformationEx = 65;
    private const int FileIdExtdDirectoryInfo = 0x13;
    private const int FileIdExtdDirectoryRestartInfo = 0x14;
    private const int DirectoryEnumerationBufferSize = 64 * 1024;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorFileExists = 80;
    private const uint StatusObjectNameCollision = 0xC0000035;
    private readonly WindowsProtectedPathSecurityPlatform _metadata = new();
    private readonly IRecoveryStoreRenameNative _renameNative;

    internal WindowsRecoveryStoreFilePlatform()
        : this(new WindowsRecoveryStoreRenameNative())
    {
    }

    internal WindowsRecoveryStoreFilePlatform(IRecoveryStoreRenameNative renameNative)
    {
        _renameNative = renameNative ?? throw new ArgumentNullException(nameof(renameNative));
    }

    public Result<SafeFileHandle> OpenDirectory(string path)
    {
        SafeFileHandle handle = NativeMethods.CreateFile(
            path,
            NativeMethods.FileReadData | NativeMethods.FileReadAttributes | NativeMethods.ReadControl,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            nint.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            nint.Zero);
        return !handle.IsInvalid
            ? Result<SafeFileHandle>.Success(handle)
            : InvalidHandle(handle, BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_OPEN_FAILED);
    }

    public Result<SafeFileHandle> CreateTemporary(
        SafeFileHandle directoryHandle,
        string leafName) => OpenRelative(
            directoryHandle,
            leafName,
            GenericRead
                | GenericWrite
                | NativeMethods.ReadControl
                | NativeMethods.WriteDac
                | WriteOwner
                | DeleteAccess
                | Synchronize,
            0,
            FileCreate,
            FileNonDirectoryFile
                | FileSynchronousIoNonalert
                | FileWriteThrough
                | FileOpenReparsePoint,
            BrokerErrorCodes.FSL_E_RECOVERY_RECORD_WRITE_FAILED);

    public Result<SafeFileHandle> OpenExisting(
        SafeFileHandle directoryHandle,
        string leafName) => OpenRelative(
            directoryHandle,
            leafName,
            GenericRead
                | NativeMethods.ReadControl
                | DeleteAccess
                | Synchronize,
            NativeMethods.FileShareRead
                | NativeMethods.FileShareWrite
                | NativeMethods.FileShareDelete,
            FileOpen,
            FileNonDirectoryFile | FileSynchronousIoNonalert | FileOpenReparsePoint,
            BrokerErrorCodes.FSL_E_RECOVERY_RECORD_NOT_FOUND);

    public Result<RecoveryRecordFileIdentity> GetIdentity(SafeFileHandle handle)
    {
        Result<FolderSessionLock.Windows.Models.DirectoryIdentity> identity = _metadata.GetIdentity(handle);
        if (identity.IsFailure
            || NativeMethods.GetFileStandardInfo(
                handle,
                NativeMethods.FileInfoByHandleClass.FileStandardInfo,
                out NativeMethods.FileStandardInfo standard,
                (uint)Marshal.SizeOf<NativeMethods.FileStandardInfo>()) == 0)
        {
            return Failure<RecoveryRecordFileIdentity>(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED);
        }

        return Result<RecoveryRecordFileIdentity>.Success(new(
            identity.Value.VolumeSerialNumber,
            identity.Value.FileIdHigh,
            identity.Value.FileIdLow,
            standard.NumberOfLinks));
    }

    public Result<NativeMethods.FileAttributeTagInfo> GetAttributes(SafeFileHandle handle) =>
        _metadata.GetAttributes(handle);

    public Result<string> GetFinalPath(SafeFileHandle handle) => _metadata.GetFinalPath(handle);

    public Result WriteAll(SafeFileHandle handle, ReadOnlyMemory<byte> bytes)
    {
        try
        {
            RandomAccess.Write(handle, bytes.Span, 0);
            return Result.Success();
        }
        catch (IOException)
        {
            return Failure(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_WRITE_FAILED);
        }
    }

    public Result Flush(SafeFileHandle handle) => FlushFileBuffers(handle)
        ? Result.Success()
        : Failure(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_WRITE_FAILED);

    public Result<byte[]> ReadAll(SafeFileHandle handle, int maximumLength)
    {
        try
        {
            long length = RandomAccess.GetLength(handle);
            if (length < 0 || length > maximumLength)
            {
                return Failure<byte[]>(RecoveryRecordErrors.TrailingData.Code);
            }

            var bytes = new byte[checked((int)length)];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
                if (count == 0)
                {
                    return Failure<byte[]>(RecoveryRecordErrors.Truncated.Code);
                }

                read += count;
            }

            return RandomAccess.GetLength(handle) == length
                ? Result<byte[]>.Success(bytes)
                : Failure<byte[]>(BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH);
        }
        catch (IOException)
        {
            return Failure<byte[]>(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_NOT_FOUND);
        }
    }

    public Result Rename(
        SafeFileHandle fileHandle,
        SafeFileHandle directoryHandle,
        string targetLeafName,
        bool replaceExisting)
    {
        if (fileHandle.IsClosed
            || fileHandle.IsInvalid
            || directoryHandle.IsClosed
            || directoryHandle.IsInvalid
            || string.IsNullOrEmpty(targetLeafName)
            || !string.Equals(
                Path.GetFileName(targetLeafName),
                targetLeafName,
                StringComparison.Ordinal))
        {
            return Failure(BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED);
        }

        byte[] name = System.Text.Encoding.Unicode.GetBytes(targetLeafName);
        int structureSize = Marshal.SizeOf<FileRenameInformation>();
        int flagsOffset = checked((int)Marshal.OffsetOf<FileRenameInformation>(
            nameof(FileRenameInformation.Flags)));
        int rootOffset = checked((int)Marshal.OffsetOf<FileRenameInformation>(
            nameof(FileRenameInformation.RootDirectory)));
        int lengthOffset = checked((int)Marshal.OffsetOf<FileRenameInformation>(
            nameof(FileRenameInformation.FileNameLength)));
        int nameOffset = checked((int)Marshal.OffsetOf<FileRenameInformation>(
            nameof(FileRenameInformation.FileName)));
        int bufferLength = checked(structureSize + name.Length);
        uint flags = replaceExisting
            ? FileRenameReplaceIfExists | FileRenamePosixSemantics
            : 0;
        nint renameBuffer = Marshal.AllocHGlobal(bufferLength);
        bool directoryHandleAddedRef = false;
        int status;
        try
        {
            Marshal.Copy(new byte[bufferLength], 0, renameBuffer, bufferLength);
            directoryHandle.DangerousAddRef(ref directoryHandleAddedRef);
            Marshal.WriteInt32(renameBuffer, flagsOffset, unchecked((int)flags));
            Marshal.WriteIntPtr(
                renameBuffer,
                rootOffset,
                directoryHandle.DangerousGetHandle());
            Marshal.WriteInt32(renameBuffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, renameBuffer + nameOffset, name.Length);
            status = _renameNative.SetRenameInformation(
                fileHandle,
                renameBuffer,
                checked((uint)bufferLength),
                FileRenameInformationEx);
        }
        finally
        {
            if (directoryHandleAddedRef)
            {
                directoryHandle.DangerousRelease();
            }

            Marshal.FreeHGlobal(renameBuffer);
        }

        if (status >= 0)
        {
            return Result.Success();
        }

        uint dosError = _renameNative.NtStatusToDosError(status);
        if (!replaceExisting
            && (unchecked((uint)status) == StatusObjectNameCollision
                || dosError is ErrorAlreadyExists or ErrorFileExists))
        {
            return Failure(BrokerErrorCodes.FSL_E_RECOVERY_FILE_ALREADY_EXISTS);
        }

        return dosError is ErrorInvalidParameter or ErrorNotSupported
            ? Failure(BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED)
            : Failure(BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED);
    }

    public Result Delete(SafeFileHandle fileHandle)
    {
        uint flags = FileDispositionDelete | FileDispositionPosixSemantics;
        nint dispositionBuffer = Marshal.AllocHGlobal(sizeof(uint));
        int error;
        try
        {
            Marshal.WriteInt32(dispositionBuffer, unchecked((int)flags));
            if (SetFileInformationByHandle(
                fileHandle,
                FileDispositionInfoEx,
                dispositionBuffer,
                sizeof(uint)))
            {
                return Result.Success();
            }

            error = Marshal.GetLastPInvokeError();
        }
        finally
        {
            Marshal.FreeHGlobal(dispositionBuffer);
        }

        return error is ErrorInvalidParameter or ErrorNotSupported
            ? Failure(BrokerErrorCodes.FSL_E_RECOVERY_FILE_HANDLE_DELETE_UNSUPPORTED)
            : Failure(BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED);
    }

    public Result CloseAfterDisposition(SafeFileHandle fileHandle)
    {
        ArgumentNullException.ThrowIfNull(fileHandle);
        fileHandle.Dispose();
        return fileHandle.IsClosed
            ? Result.Success()
            : Failure(BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED);
    }

    public Result<RecoveryRecordFileIdentity?> GetLeafIdentity(
        SafeFileHandle directoryHandle,
        string leafName)
    {
        if (directoryHandle.IsClosed
            || directoryHandle.IsInvalid
            || string.IsNullOrEmpty(leafName)
            || !string.Equals(Path.GetFileName(leafName), leafName, StringComparison.Ordinal))
        {
            return Failure<RecoveryRecordFileIdentity?>(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED);
        }

        Result<RecoveryRecordFileIdentity> directoryIdentity = GetIdentity(directoryHandle);
        if (directoryIdentity.IsFailure)
        {
            return Result<RecoveryRecordFileIdentity?>.Failure(directoryIdentity.Error!);
        }

        int nextOffset = checked((int)Marshal.OffsetOf<FileIdExtdDirectoryInformation>(
            nameof(FileIdExtdDirectoryInformation.NextEntryOffset)));
        int nameLengthOffset = checked((int)Marshal.OffsetOf<FileIdExtdDirectoryInformation>(
            nameof(FileIdExtdDirectoryInformation.FileNameLength)));
        int fileIdOffset = checked((int)Marshal.OffsetOf<FileIdExtdDirectoryInformation>(
            nameof(FileIdExtdDirectoryInformation.FileIdLow)));
        int nameOffset = checked((int)Marshal.OffsetOf<FileIdExtdDirectoryInformation>(
            nameof(FileIdExtdDirectoryInformation.FileName)));
        nint buffer = Marshal.AllocHGlobal(DirectoryEnumerationBufferSize);
        bool restart = true;
        try
        {
            while (true)
            {
                Marshal.Copy(
                    new byte[DirectoryEnumerationBufferSize],
                    0,
                    buffer,
                    DirectoryEnumerationBufferSize);
                if (!GetFileInformationByHandleEx(
                    directoryHandle,
                    restart ? FileIdExtdDirectoryRestartInfo : FileIdExtdDirectoryInfo,
                    buffer,
                    DirectoryEnumerationBufferSize))
                {
                    int error = Marshal.GetLastPInvokeError();
                    return error == ErrorNoMoreFiles
                        ? Result<RecoveryRecordFileIdentity?>.Success(null)
                        : Failure<RecoveryRecordFileIdentity?>(
                            BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED);
                }

                restart = false;
                int entryOffset = 0;
                while (true)
                {
                    if (entryOffset < 0
                        || entryOffset > DirectoryEnumerationBufferSize - nameOffset)
                    {
                        return Failure<RecoveryRecordFileIdentity?>(
                            BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED);
                    }

                    uint entryNameLength = unchecked((uint)Marshal.ReadInt32(
                        buffer,
                        checked(entryOffset + nameLengthOffset)));
                    int remaining = DirectoryEnumerationBufferSize - entryOffset - nameOffset;
                    if ((entryNameLength & 1) != 0 || entryNameLength > remaining)
                    {
                        return Failure<RecoveryRecordFileIdentity?>(
                            BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED);
                    }

                    var nameBytes = new byte[checked((int)entryNameLength)];
                    Marshal.Copy(
                        buffer + entryOffset + nameOffset,
                        nameBytes,
                        0,
                        nameBytes.Length);
                    string entryName = System.Text.Encoding.Unicode.GetString(nameBytes);
                    if (string.Equals(entryName, leafName, StringComparison.Ordinal))
                    {
                        var fileId = new byte[16];
                        Marshal.Copy(
                            buffer + entryOffset + fileIdOffset,
                            fileId,
                            0,
                            fileId.Length);
                        DirectoryIdentity identity = DirectoryIdentity.FromFileId(
                            directoryIdentity.Value.VolumeSerialNumber,
                            fileId);
                        return Result<RecoveryRecordFileIdentity?>.Success(new(
                            identity.VolumeSerialNumber,
                            identity.FileIdHigh,
                            identity.FileIdLow,
                            1));
                    }

                    uint nextEntryOffset = unchecked((uint)Marshal.ReadInt32(
                        buffer,
                        checked(entryOffset + nextOffset)));
                    if (nextEntryOffset == 0)
                    {
                        break;
                    }

                    if (nextEntryOffset > DirectoryEnumerationBufferSize - entryOffset)
                    {
                        return Failure<RecoveryRecordFileIdentity?>(
                            BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED);
                    }

                    entryOffset = checked(entryOffset + (int)nextEntryOffset);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Result<SafeFileHandle> OpenRelative(
        SafeFileHandle directoryHandle,
        string leafName,
        uint desiredAccess,
        uint shareAccess,
        uint disposition,
        uint options,
        string failureCode)
    {
        if (Path.GetFileName(leafName) != leafName)
        {
            return Failure<SafeFileHandle>(failureCode);
        }

        nint nameBuffer = Marshal.StringToHGlobalUni(leafName);
        try
        {
            var name = new UnicodeString
            {
                Length = checked((ushort)(leafName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((leafName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer,
            };
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = directoryHandle.DangerousGetHandle(),
                ObjectName = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>()),
                Attributes = 0x00000040,
            };
            try
            {
                Marshal.StructureToPtr(name, attributes.ObjectName, false);
                int status = NtCreateFile(
                    out nint rawHandle,
                    desiredAccess,
                    ref attributes,
                    out _,
                    nint.Zero,
                    0,
                    shareAccess,
                    disposition,
                    options,
                    nint.Zero,
                    0);
                if (status >= 0)
                {
                    return Result<SafeFileHandle>.Success(
                        new SafeFileHandle(rawHandle, ownsHandle: true));
                }

                return Failure<SafeFileHandle>(
                    unchecked((uint)status) == StatusObjectNameCollision
                        ? BrokerErrorCodes.FSL_E_RECOVERY_FILE_ALREADY_EXISTS
                        : failureCode);
            }
            finally
            {
                Marshal.FreeHGlobal(attributes.ObjectName);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static Result<SafeFileHandle> InvalidHandle(SafeFileHandle handle, string code)
    {
        handle.Dispose();
        return Failure<SafeFileHandle>(code);
    }

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(new Error(
        code,
        code,
        ErrorCategory.UnrecoverableError));

    private static Result Failure(string code) => Result.Failure(new Error(
        code,
        code,
        ErrorCategory.UnrecoverableError));

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
    internal struct FileRenameInformation
    {
        internal uint Flags;
        internal nint RootDirectory;
        internal uint FileNameLength;
        internal char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdExtdDirectoryInformation
    {
        internal uint NextEntryOffset;
        internal uint FileIndex;
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal long EndOfFile;
        internal long AllocationSize;
        internal uint FileAttributes;
        internal uint FileNameLength;
        internal uint EaSize;
        internal uint ReparsePointTag;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
        internal char FileName;
    }

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
        nint eaBuffer,
        uint eaLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        nint fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        nint fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle fileHandle);

    private sealed class WindowsRecoveryStoreRenameNative : IRecoveryStoreRenameNative
    {
        public int SetRenameInformation(
            SafeFileHandle fileHandle,
            nint fileInformation,
            uint length,
            int fileInformationClass) => NtSetInformationFile(
                fileHandle,
                out _,
                fileInformation,
                length,
                fileInformationClass);

        public uint NtStatusToDosError(int status) => RtlNtStatusToDosError(status);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            nint fileInformation,
            uint length,
            int fileInformationClass);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);
    }
}
