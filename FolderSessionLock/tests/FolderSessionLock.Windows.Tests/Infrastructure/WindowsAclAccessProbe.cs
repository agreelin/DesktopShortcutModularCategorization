using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Tests.Infrastructure;

internal static partial class WindowsAclAccessProbe
{
    internal const uint FileReadData = 0x00000001;
    internal const uint FileWriteData = 0x00000002;
    internal const uint FileAppendData = 0x00000004;
    internal const uint FileReadEa = 0x00000008;
    internal const uint FileWriteEa = 0x00000010;
    internal const uint FileExecute = 0x00000020;
    internal const uint FileDeleteChild = 0x00000040;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileWriteAttributes = 0x00000100;
    internal const uint Delete = 0x00010000;
    internal const uint ReadControl = 0x00020000;
    internal const uint WriteDac = 0x00040000;
    internal const uint WriteOwner = 0x00080000;
    internal const uint Synchronize = 0x00100000;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    internal static AccessProbeResult Probe(string path, uint access, bool directory)
    {
        using SafeFileHandle handle = Open(path, access, directory);
        return handle.IsInvalid
            ? new AccessProbeResult(false, Marshal.GetLastPInvokeError())
            : new AccessProbeResult(true, 0);
    }

    internal static SafeFileHandle Open(string path, uint access, bool directory) =>
        CreateFile(
            path,
            access,
            FileShareRead | FileShareWrite | FileShareDelete,
            nint.Zero,
            OpenExisting,
            directory ? FileFlagBackupSemantics : 0,
            nint.Zero);

    internal static AccessProbeResult RemoveDirectory(string path) =>
        RemoveDirectoryNative(path) != 0
            ? new AccessProbeResult(true, 0)
            : new AccessProbeResult(false, Marshal.GetLastPInvokeError());

    internal static AccessProbeResult MoveFile(string source, string destination) =>
        MoveFileEx(source, destination, 0) != 0
            ? new AccessProbeResult(true, 0)
            : new AccessProbeResult(false, Marshal.GetLastPInvokeError());

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "RemoveDirectoryW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RemoveDirectoryNative(string pathName);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);

}

internal readonly record struct AccessProbeResult(bool Success, int WindowsError);
