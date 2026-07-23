using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Tests.Infrastructure;

internal sealed partial class TemporaryDirectoryJunction : IDisposable
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private bool _disposed;

    private TemporaryDirectoryJunction(string path)
    {
        Path = path;
    }

    internal string Path { get; }

    internal static TemporaryDirectoryJunction Create(
        TemporaryTestDirectory directory,
        string junctionName,
        string targetDirectoryName)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ValidateLeafName(junctionName, nameof(junctionName));
        ValidateLeafName(targetDirectoryName, nameof(targetDirectoryName));

        string root = System.IO.Path.GetFullPath(directory.Path);
        string junctionPath = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(root, junctionName));
        string targetPath = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(root, targetDirectoryName));
        if (!string.Equals(
                Directory.GetParent(junctionPath)!.FullName,
                root,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Directory.GetParent(targetPath)!.FullName,
                root,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Junction paths must remain inside the temporary test directory.");
        }

        if (!Directory.Exists(targetPath))
        {
            throw new DirectoryNotFoundException(
                $"The junction target does not exist: '{targetPath}'.");
        }

        Directory.CreateDirectory(junctionPath);
        try
        {
            SetMountPoint(junctionPath, targetPath);
            return new TemporaryDirectoryJunction(junctionPath);
        }
        catch
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (NativeMethods.RemoveDirectory(Path) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw CreateIOException("RemoveDirectoryW", error, Path);
        }

        _disposed = true;
    }

    private static void SetMountPoint(string junctionPath, string targetPath)
    {
        using SafeFileHandle handle = NativeMethods.CreateFile(
            junctionPath,
            GenericWrite,
            FileShareRead | FileShareWrite | FileShareDelete,
            nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            nint.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            throw CreateIOException("CreateFileW", error, junctionPath);
        }

        byte[] buffer = CreateMountPointBuffer(targetPath);
        nint bufferPointer = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, bufferPointer, buffer.Length);
            if (NativeMethods.DeviceIoControl(
                    handle,
                    FsctlSetReparsePoint,
                    bufferPointer,
                    (uint)buffer.Length,
                    nint.Zero,
                    0,
                    out _,
                    nint.Zero) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw CreateIOException(
                    "DeviceIoControl(FSCTL_SET_REPARSE_POINT)",
                    error,
                    junctionPath);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(bufferPointer);
        }
    }

    private static byte[] CreateMountPointBuffer(string targetPath)
    {
        byte[] substituteName = Encoding.Unicode.GetBytes($@"\??\{targetPath}");
        byte[] printName = Encoding.Unicode.GetBytes(targetPath);
        int reparseDataLength = checked(
            8 + substituteName.Length + sizeof(char) + printName.Length + sizeof(char));
        var buffer = new byte[checked(8 + reparseDataLength)];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(4, 2),
            checked((ushort)reparseDataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(10, 2),
            checked((ushort)substituteName.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(12, 2),
            checked((ushort)(substituteName.Length + sizeof(char))));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(14, 2),
            checked((ushort)printName.Length));
        substituteName.CopyTo(buffer, 16);
        printName.CopyTo(buffer, 16 + substituteName.Length + sizeof(char));

        return buffer;
    }

    private static IOException CreateIOException(string operation, int error, string path) =>
        new(
            $"{operation} failed with Windows error {error} for '{path}'.",
            new Win32Exception(error));

    private static void ValidateLeafName(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        if (!string.Equals(System.IO.Path.GetFileName(name), name, StringComparison.Ordinal)
            || name is "." or "..")
        {
            throw new ArgumentException("A single directory name is required.", parameterName);
        }
    }

    private static partial class NativeMethods
    {
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

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            nint inputBuffer,
            uint inputBufferSize,
            nint outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            nint overlapped);

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "RemoveDirectoryW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int RemoveDirectory(string pathName);
    }
}
