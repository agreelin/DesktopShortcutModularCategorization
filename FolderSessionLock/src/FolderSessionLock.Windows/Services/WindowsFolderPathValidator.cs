using System.Runtime.InteropServices;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Services;

public sealed class WindowsFolderPathValidator
{
    private static readonly Error EmptyPathError = new(
        "windows.path.empty",
        "A directory path is required.",
        ErrorCategory.ValidationFailed);

    private static readonly Error RelativePathError = new(
        "windows.path.relative",
        "The directory path must be fully qualified.",
        ErrorCategory.ValidationFailed);

    private static readonly Error InvalidPathError = new(
        "windows.path.invalid",
        "The directory path is invalid.",
        ErrorCategory.ValidationFailed);

    private static readonly Error UncPathError = new(
        "windows.path.unc",
        "UNC and device namespace paths are not supported.",
        ErrorCategory.UnsupportedPath);

    private static readonly Error RootPathError = new(
        "windows.path.root",
        "A volume root cannot be locked.",
        ErrorCategory.UnsupportedPath);

    private static readonly Error NotDirectoryError = new(
        "windows.path.not_directory",
        "The path does not identify a directory.",
        ErrorCategory.ValidationFailed);

    private static readonly Error NonFixedDriveError = new(
        "windows.path.drive_not_fixed",
        "The directory must be on a fixed local drive.",
        ErrorCategory.UnsupportedPath);

    private static readonly Error ReparsePointError = new(
        "windows.path.reparse_point",
        "The directory or one of its ancestors is a reparse point.",
        ErrorCategory.UnsupportedPath);

    private static readonly Error NonNtfsError = new(
        "windows.path.file_system_not_ntfs",
        "The directory must be on an NTFS file system.",
        ErrorCategory.UnsupportedPath);

    private static readonly Error FinalPathMismatchError = new(
        "windows.path.final_path_mismatch",
        "The opened directory does not map to the normalized input path.",
        ErrorCategory.UnsupportedPath);

    private static readonly Error PathMappingChangedError = new(
        "windows.path.mapping_changed",
        "The current path no longer maps to the validated directory.",
        ErrorCategory.UnsupportedPath);

    private static readonly Error RepositoryForbiddenError = new(
        BrokerErrorCodes.FSL_E_PATH_REPOSITORY_FORBIDDEN,
        BrokerErrorCodes.FSL_E_PATH_REPOSITORY_FORBIDDEN,
        ErrorCategory.UnsupportedPath);

    private static readonly Error SynchronizationForbiddenError = new(
        BrokerErrorCodes.FSL_E_PATH_SYNCHRONIZATION_ROOT_FORBIDDEN,
        BrokerErrorCodes.FSL_E_PATH_SYNCHRONIZATION_ROOT_FORBIDDEN,
        ErrorCategory.UnsupportedPath);

    private readonly FolderPathSafetyPolicy _safetyPolicy;
    private readonly WindowsFolderPathPlatform _platform;
    private readonly IRepositoryRootClassifier? _repositoryClassifier;
    private readonly ISynchronizationRootClassifier? _synchronizationClassifier;

    public WindowsFolderPathValidator(FolderPathSafetyPolicy safetyPolicy)
        : this(safetyPolicy, new WindowsFolderPathPlatform(), null, null)
    {
    }

    public WindowsFolderPathValidator(
        FolderPathSafetyPolicy safetyPolicy,
        IRepositoryRootClassifier repositoryClassifier,
        ISynchronizationRootClassifier synchronizationClassifier)
        : this(
            safetyPolicy,
            new WindowsFolderPathPlatform(),
            repositoryClassifier,
            synchronizationClassifier)
    {
    }

    internal WindowsFolderPathValidator(
        FolderPathSafetyPolicy safetyPolicy,
        WindowsFolderPathPlatform platform,
        IRepositoryRootClassifier? repositoryClassifier = null,
        ISynchronizationRootClassifier? synchronizationClassifier = null)
    {
        _safetyPolicy = safetyPolicy ?? throw new ArgumentNullException(nameof(safetyPolicy));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _repositoryClassifier = repositoryClassifier;
        _synchronizationClassifier = synchronizationClassifier;
    }

    public Result<ValidatedDirectory> Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<ValidatedDirectory>.Failure(EmptyPathError);
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return Result<ValidatedDirectory>.Failure(RelativePathError);
        }

        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return Result<ValidatedDirectory>.Failure(UncPathError);
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<ValidatedDirectory>.Failure(InvalidPathError);
        }

        string rootPath = Path.GetPathRoot(normalizedPath)!;
        if (string.Equals(normalizedPath, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return Result<ValidatedDirectory>.Failure(RootPathError);
        }

        Result safetyResult = _safetyPolicy.Validate(normalizedPath);
        if (safetyResult.IsFailure)
        {
            return Result<ValidatedDirectory>.Failure(safetyResult.Error!);
        }

        if (_platform.GetDriveType(rootPath) != NativeMethods.DriveFixed)
        {
            return Result<ValidatedDirectory>.Failure(NonFixedDriveError);
        }

        foreach (string componentPath in EnumerateComponents(normalizedPath, rootPath))
        {
            Result<SafeFileHandle> componentHandleResult = _platform.OpenPath(
                componentPath,
                NativeMethods.FileReadAttributes);
            if (componentHandleResult.IsFailure)
            {
                return Result<ValidatedDirectory>.Failure(componentHandleResult.Error!);
            }

            using SafeFileHandle componentHandle = componentHandleResult.Value;
            Result<NativeMethods.FileAttributeTagInfo> attributeResult =
                _platform.GetAttributeTagInfo(componentHandle);
            if (attributeResult.IsFailure)
            {
                return Result<ValidatedDirectory>.Failure(attributeResult.Error!);
            }

            if ((attributeResult.Value.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
            {
                return Result<ValidatedDirectory>.Failure(ReparsePointError);
            }

            if (string.Equals(componentPath, normalizedPath, StringComparison.OrdinalIgnoreCase)
                && (attributeResult.Value.FileAttributes & NativeMethods.FileAttributeDirectory) == 0)
            {
                return Result<ValidatedDirectory>.Failure(NotDirectoryError);
            }
        }

        Result<SafeFileHandle> handleResult = _platform.OpenPath(
            normalizedPath,
            NativeMethods.FileReadAttributes
                | NativeMethods.ReadControl
                | NativeMethods.WriteDac);
        if (handleResult.IsFailure)
        {
            return Result<ValidatedDirectory>.Failure(handleResult.Error!);
        }

        SafeFileHandle handle = handleResult.Value;
        try
        {
            Result<NativeMethods.FileAttributeTagInfo> finalAttributeResult =
                _platform.GetAttributeTagInfo(handle);
            if (finalAttributeResult.IsFailure)
            {
                return Result<ValidatedDirectory>.Failure(finalAttributeResult.Error!);
            }

            if ((finalAttributeResult.Value.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
            {
                return Result<ValidatedDirectory>.Failure(ReparsePointError);
            }

            if ((finalAttributeResult.Value.FileAttributes & NativeMethods.FileAttributeDirectory) == 0)
            {
                return Result<ValidatedDirectory>.Failure(NotDirectoryError);
            }

            Result<string> finalPathResult = _platform.GetFinalPath(handle);
            if (finalPathResult.IsFailure)
            {
                return Result<ValidatedDirectory>.Failure(finalPathResult.Error!);
            }

            string finalPath = NormalizeFinalPath(finalPathResult.Value);
            if (!string.Equals(normalizedPath, finalPath, StringComparison.OrdinalIgnoreCase))
            {
                return Result<ValidatedDirectory>.Failure(FinalPathMismatchError);
            }

            Result<string> fileSystemResult = _platform.GetFileSystemName(handle);
            if (fileSystemResult.IsFailure)
            {
                return Result<ValidatedDirectory>.Failure(fileSystemResult.Error!);
            }

            if (!string.Equals(fileSystemResult.Value, "NTFS", StringComparison.Ordinal))
            {
                return Result<ValidatedDirectory>.Failure(NonNtfsError);
            }

            Result<DirectoryIdentity> identityResult = _platform.GetDirectoryIdentity(handle);
            if (identityResult.IsFailure)
            {
                return Result<ValidatedDirectory>.Failure(identityResult.Error!);
            }

            var validatedDirectory = new ValidatedDirectory(
                normalizedPath,
                finalPath,
                identityResult.Value,
                handle,
                hasReadControl: true,
                hasWriteDac: true);
            Result classification = Classify(validatedDirectory);
            if (classification.IsFailure)
            {
                validatedDirectory.Dispose();
                return Result<ValidatedDirectory>.Failure(classification.Error!);
            }

            handle = null!;
            return Result<ValidatedDirectory>.Success(validatedDirectory);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private Result Classify(ValidatedDirectory directory)
    {
        if (_repositoryClassifier is not null)
        {
            Result<bool> repository = _repositoryClassifier.IsUnderRepositoryRoot(directory);
            if (repository.IsFailure)
            {
                return Result.Failure(repository.Error!);
            }

            if (repository.Value)
            {
                return Result.Failure(RepositoryForbiddenError);
            }
        }

        if (_synchronizationClassifier is not null)
        {
            Result<bool> synchronization =
                _synchronizationClassifier.IsUnderSynchronizationRoot(directory);
            if (synchronization.IsFailure)
            {
                return Result.Failure(synchronization.Error!);
            }

            if (synchronization.Value)
            {
                return Result.Failure(SynchronizationForbiddenError);
            }
        }

        return Result.Success();
    }

    internal Result VerifyCurrentPathMapping(ValidatedDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        string rootPath = Path.GetPathRoot(directory.NormalizedPath)!;
        foreach (string componentPath in EnumerateComponents(directory.NormalizedPath, rootPath))
        {
            Result<SafeFileHandle> componentHandleResult = _platform.OpenPath(componentPath, 0);
            if (componentHandleResult.IsFailure)
            {
                return Result.Failure(componentHandleResult.Error!);
            }

            using SafeFileHandle componentHandle = componentHandleResult.Value;
            Result<NativeMethods.FileAttributeTagInfo> attributeResult =
                _platform.GetAttributeTagInfo(componentHandle);
            if (attributeResult.IsFailure)
            {
                return Result.Failure(attributeResult.Error!);
            }

            if ((attributeResult.Value.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
            {
                return Result.Failure(ReparsePointError);
            }
        }

        Result<SafeFileHandle> checkHandleResult = _platform.OpenPath(directory.NormalizedPath, 0);
        if (checkHandleResult.IsFailure)
        {
            return Result.Failure(checkHandleResult.Error!);
        }

        using SafeFileHandle checkHandle = checkHandleResult.Value;
        Result<string> finalPathResult = _platform.GetFinalPath(checkHandle);
        if (finalPathResult.IsFailure)
        {
            return Result.Failure(finalPathResult.Error!);
        }

        Result<DirectoryIdentity> identityResult = _platform.GetDirectoryIdentity(checkHandle);
        if (identityResult.IsFailure)
        {
            return Result.Failure(identityResult.Error!);
        }

        string finalPath = NormalizeFinalPath(finalPathResult.Value);
        return string.Equals(directory.FinalPath, finalPath, StringComparison.OrdinalIgnoreCase)
            && directory.Identity == identityResult.Value
                ? Result.Success()
                : Result.Failure(PathMappingChangedError);
    }

    private static IEnumerable<string> EnumerateComponents(string path, string root)
    {
        yield return root;

        string current = root;
        foreach (string component in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            yield return current;
        }
    }

    private static string NormalizeFinalPath(string finalPath)
    {
        const string localPrefix = "\\\\?\\";
        const string uncPrefix = "\\\\?\\UNC\\";

        string dosPath = finalPath.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"\\\\{finalPath[uncPrefix.Length..]}"
            : finalPath.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
                ? finalPath[localPrefix.Length..]
                : finalPath;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dosPath));
    }
}

internal class WindowsFolderPathPlatform
{
    private const int FileSystemNameCapacity = 64;

    internal virtual uint GetDriveType(string rootPath) => NativeMethods.GetDriveType(rootPath);

    internal virtual Result<SafeFileHandle> OpenPath(string path, uint desiredAccess)
    {
        SafeFileHandle handle = NativeMethods.CreateFile(
            path,
            desiredAccess,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            nint.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            nint.Zero);
        if (!handle.IsInvalid)
        {
            return Result<SafeFileHandle>.Success(handle);
        }

        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return error switch
        {
            NativeMethods.ErrorAccessDenied => Result<SafeFileHandle>.Failure(new Error(
                "windows.path.insufficient_permissions",
                $"CreateFileW denied directory access with Windows error {error}.",
                ErrorCategory.InsufficientPermissions)),
            NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound =>
                Result<SafeFileHandle>.Failure(new Error(
                    "windows.path.not_found",
                    $"CreateFileW could not find the path; Windows error {error}.",
                    ErrorCategory.ValidationFailed)),
            _ => NativeFailure<SafeFileHandle>("CreateFileW", error),
        };
    }

    internal virtual Result<NativeMethods.FileAttributeTagInfo> GetAttributeTagInfo(
        SafeFileHandle handle)
    {
        if (NativeMethods.GetFileAttributeTagInfo(
                handle,
                NativeMethods.FileInfoByHandleClass.FileAttributeTagInfo,
                out NativeMethods.FileAttributeTagInfo information,
                (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInfo>()) == 0)
        {
            return NativeFailure<NativeMethods.FileAttributeTagInfo>(
                "GetFileInformationByHandleEx(FileAttributeTagInfo)",
                Marshal.GetLastPInvokeError());
        }

        return Result<NativeMethods.FileAttributeTagInfo>.Success(information);
    }

    internal virtual unsafe Result<string> GetFileSystemName(SafeFileHandle handle)
    {
        var buffer = new char[FileSystemNameCapacity];
        fixed (char* bufferPointer = buffer)
        {
            if (NativeMethods.GetVolumeInformationByHandle(
                    handle,
                    nint.Zero,
                    0,
                    out _,
                    out _,
                    out _,
                    bufferPointer,
                    (uint)buffer.Length) == 0)
            {
                return NativeFailure<string>(
                    "GetVolumeInformationByHandleW",
                    Marshal.GetLastPInvokeError());
            }

            return Result<string>.Success(new string(bufferPointer));
        }
    }

    internal virtual unsafe Result<string> GetFinalPath(SafeFileHandle handle)
    {
        uint capacity = 260;
        while (true)
        {
            var buffer = new char[capacity];
            fixed (char* bufferPointer = buffer)
            {
                uint length = NativeMethods.GetFinalPathNameByHandle(
                    handle,
                    bufferPointer,
                    capacity,
                    0);
                if (length == 0)
                {
                    return NativeFailure<string>(
                        "GetFinalPathNameByHandleW",
                        Marshal.GetLastPInvokeError());
                }

                if (length < capacity)
                {
                    return Result<string>.Success(new string(bufferPointer, 0, (int)length));
                }

                capacity = checked(length + 1);
            }
        }
    }

    internal virtual unsafe Result<DirectoryIdentity> GetDirectoryIdentity(SafeFileHandle handle)
    {
        if (NativeMethods.GetFileIdInfo(
                handle,
                NativeMethods.FileInfoByHandleClass.FileIdInfo,
                out NativeMethods.FileIdInfo information,
                (uint)Marshal.SizeOf<NativeMethods.FileIdInfo>()) == 0)
        {
            return NativeFailure<DirectoryIdentity>(
                "GetFileInformationByHandleEx(FileIdInfo)",
                Marshal.GetLastPInvokeError());
        }

        byte* identifier = information.FileId.Identifier;
        return Result<DirectoryIdentity>.Success(DirectoryIdentity.FromFileId(
            information.VolumeSerialNumber,
            new ReadOnlySpan<byte>(identifier, 16)));
    }

    private static Result<T> NativeFailure<T>(string operation, int error) =>
        Result<T>.Failure(new Error(
            "windows.path.native_call_failed",
            $"{operation} failed with Windows error {error}.",
            ErrorCategory.PlatformError));
}
