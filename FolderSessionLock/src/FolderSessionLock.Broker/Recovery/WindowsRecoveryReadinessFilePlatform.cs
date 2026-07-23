using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery;

internal interface IRecoveryReadinessFilePlatform
{
    string ReadinessDirectory { get; }

    Result<SafeFileHandle> OpenDirectory();
    Result<SafeFileHandle> CreateTemporary(SafeFileHandle directoryHandle, string leafName);
    Result<SafeFileHandle> OpenExisting(SafeFileHandle directoryHandle, string leafName);
    Result<RecoveryRecordFileIdentity> GetIdentity(SafeFileHandle handle);
    Result<NativeMethods.FileAttributeTagInfo> GetAttributes(SafeFileHandle handle);
    Result<string> GetFinalPath(SafeFileHandle handle);
    Result WriteAll(SafeFileHandle handle, ReadOnlyMemory<byte> bytes);
    Result Flush(SafeFileHandle handle);
    Result<byte[]> ReadAll(SafeFileHandle handle, int maximumLength);
    Result Rename(SafeFileHandle fileHandle, SafeFileHandle directoryHandle, string leafName);
    Result Delete(SafeFileHandle fileHandle);
    Result CloseAfterDisposition(SafeFileHandle fileHandle);
    Result<RecoveryRecordFileIdentity?> GetLeafIdentity(SafeFileHandle directoryHandle, string leafName);
}

internal sealed class WindowsRecoveryReadinessFilePlatform : IRecoveryReadinessFilePlatform
{
    private readonly IRecoveryStoreFilePlatform _files;

    internal WindowsRecoveryReadinessFilePlatform()
        : this(
            Path.Combine(
                WindowsKnownFolderPath.GetRequiredPath(WindowsKnownFolderPath.ProgramData),
                "FolderSessionLock",
                "Readiness"),
            new WindowsRecoveryStoreFilePlatform())
    {
    }

    internal WindowsRecoveryReadinessFilePlatform(
        string readinessDirectory,
        IRecoveryStoreFilePlatform files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readinessDirectory);
        if (!Path.IsPathFullyQualified(readinessDirectory))
        {
            throw new ArgumentException(
                "The readiness directory must be fully qualified.",
                nameof(readinessDirectory));
        }

        ReadinessDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(readinessDirectory));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public string ReadinessDirectory { get; }

    public Result<SafeFileHandle> OpenDirectory() => _files.OpenDirectory(ReadinessDirectory);

    public Result<SafeFileHandle> CreateTemporary(
        SafeFileHandle directoryHandle,
        string leafName) => _files.CreateTemporary(directoryHandle, leafName);

    public Result<SafeFileHandle> OpenExisting(
        SafeFileHandle directoryHandle,
        string leafName) => _files.OpenExisting(directoryHandle, leafName);

    public Result<RecoveryRecordFileIdentity> GetIdentity(SafeFileHandle handle) =>
        _files.GetIdentity(handle);

    public Result<NativeMethods.FileAttributeTagInfo> GetAttributes(SafeFileHandle handle) =>
        _files.GetAttributes(handle);

    public Result<string> GetFinalPath(SafeFileHandle handle) => _files.GetFinalPath(handle);

    public Result WriteAll(SafeFileHandle handle, ReadOnlyMemory<byte> bytes) =>
        _files.WriteAll(handle, bytes);

    public Result Flush(SafeFileHandle handle) => _files.Flush(handle);

    public Result<byte[]> ReadAll(SafeFileHandle handle, int maximumLength) =>
        _files.ReadAll(handle, maximumLength);

    public Result Rename(
        SafeFileHandle fileHandle,
        SafeFileHandle directoryHandle,
        string leafName) => _files.Rename(
            fileHandle,
            directoryHandle,
            leafName,
            replaceExisting: true);

    public Result Delete(SafeFileHandle fileHandle) => _files.Delete(fileHandle);

    public Result CloseAfterDisposition(SafeFileHandle fileHandle) =>
        _files.CloseAfterDisposition(fileHandle);

    public Result<RecoveryRecordFileIdentity?> GetLeafIdentity(
        SafeFileHandle directoryHandle,
        string leafName) => _files.GetLeafIdentity(directoryHandle, leafName);
}
