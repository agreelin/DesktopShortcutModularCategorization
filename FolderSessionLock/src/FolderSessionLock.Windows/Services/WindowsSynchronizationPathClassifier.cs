using System.Runtime.InteropServices;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Services;

public sealed class WindowsSynchronizationPathClassifier : ISynchronizationRootClassifier
{
    private readonly WindowsSynchronizationPathPlatform _platform;

    internal WindowsSynchronizationPathClassifier(IInitiatingUserTokenSource tokenSource)
        : this(new WindowsSynchronizationPathPlatform(tokenSource))
    {
    }

    internal WindowsSynchronizationPathClassifier(WindowsSynchronizationPathPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public Result<bool> IsUnderSynchronizationRoot(ValidatedDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Result<bool> cloud = _platform.IsUnderCloudFilesSyncRoot(directory.Handle);
        if (cloud.IsFailure || cloud.Value)
        {
            return cloud;
        }

        Result<KnownFolderLookup> lookup = _platform.GetInitiatingUserSkyDrivePath();
        if (lookup.IsFailure)
        {
            return Failure();
        }

        if (!lookup.Value.Exists)
        {
            return Result<bool>.Success(false);
        }

        Result<SafeFileHandle> open = _platform.OpenDirectory(lookup.Value.Path!);
        if (open.IsFailure)
        {
            return Failure();
        }

        using SafeFileHandle skyDrive = open.Value;
        Result<NativeMethods.FileAttributeTagInfo> attributes = _platform.GetAttributes(skyDrive);
        Result<string> finalPath = _platform.GetFinalPath(skyDrive);
        Result<DirectoryIdentity> before = _platform.GetIdentity(skyDrive);
        if (attributes.IsFailure
            || (attributes.Value.FileAttributes & NativeMethods.FileAttributeDirectory) == 0
            || (attributes.Value.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0
            || finalPath.IsFailure
            || before.IsFailure)
        {
            return Failure();
        }

        string skyDrivePath = NormalizeFinalPath(finalPath.Value);
        bool matches = IsSameOrDescendant(skyDrivePath, directory.FinalPath);
        Result<DirectoryIdentity> after = _platform.GetIdentity(skyDrive);
        Result<DirectoryIdentity> target = _platform.GetIdentity(directory.Handle);
        if (after.IsFailure
            || target.IsFailure
            || before.Value != after.Value
            || target.Value != directory.Identity)
        {
            return Failure();
        }

        return Result<bool>.Success(matches);
    }

    private static bool IsSameOrDescendant(string root, string path)
    {
        string normalizedRoot = NormalizeFinalPath(root);
        string normalizedPath = NormalizeFinalPath(path);
        string rootVolume = Path.GetPathRoot(normalizedRoot)!;
        string pathVolume = Path.GetPathRoot(normalizedPath)!;
        if (!string.Equals(rootVolume, pathVolume, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] rootComponents = normalizedRoot[rootVolume.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string[] pathComponents = normalizedPath[pathVolume.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return rootComponents.Length <= pathComponents.Length
            && rootComponents.SequenceEqual(
                pathComponents.Take(rootComponents.Length),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeFinalPath(string path)
    {
        const string prefix = @"\\?\";
        string value = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : path;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static Result<bool> Failure() => Result<bool>.Failure(new Error(
        BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
        BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
        ErrorCategory.UnrecoverableError));
}

internal sealed record KnownFolderLookup(
    bool Exists,
    string? Path,
    string? Reason = null);

internal class WindowsSynchronizationPathPlatform
{
    private const int CloudSyncRootInfoStandard = 1;
    internal const int ErrorCloudFileNotUnderSyncRoot = 390;
    internal const int HResultFromWin32CloudFileNotUnderSyncRoot = unchecked((int)0x80070186);
    internal const int StatusCloudFileNotUnderSyncRoot = unchecked((int)0xC000CF13);
    internal const int HResultFromNtCloudFileNotUnderSyncRoot = unchecked((int)0xD000CF13);
    internal const int HResultFileNotFound = unchecked((int)0x80070002);
    internal const int HResultPathNotFound = unchecked((int)0x80070003);
    internal const int EInvalidArg = unchecked((int)0x80070057);
    internal const uint KnownFolderFlagsDefault = 0;
    private const int BufferLength = 4096;
    private readonly IInitiatingUserTokenSource _tokenSource;
    private readonly WindowsFolderPathPlatform _paths = new();

    internal WindowsSynchronizationPathPlatform(IInitiatingUserTokenSource tokenSource)
    {
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
    }

    internal virtual Result<bool> IsUnderCloudFilesSyncRoot(SafeFileHandle targetHandle)
    {
        nint buffer = Marshal.AllocHGlobal(BufferLength);
        try
        {
            int hresult = CfGetSyncRootInfoByHandle(
                targetHandle,
                CloudSyncRootInfoStandard,
                buffer,
                BufferLength,
                out _);
            if (hresult >= 0)
            {
                return Result<bool>.Success(true);
            }

            if (IsNotUnderSyncRoot(hresult))
            {
                return Result<bool>.Success(false);
            }

            return Failure<bool>();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal virtual Result<KnownFolderLookup> GetInitiatingUserSkyDrivePath()
    {
        Result<Guid[]> registered = GetRegisteredKnownFolderIds();
        if (registered.IsFailure)
        {
            return Failure<KnownFolderLookup>();
        }

        if (!registered.Value.Contains(WindowsKnownFolderPath.SkyDrive))
        {
            return Result<KnownFolderLookup>.Success(new(
                false,
                null,
                "KnownFolderNotRegistered"));
        }

        Result<SafeAccessTokenHandle> token = _tokenSource.GetToken();
        if (token.IsFailure || token.Value.IsClosed || token.Value.IsInvalid)
        {
            return Failure<KnownFolderLookup>();
        }

        bool addedRef = false;
        try
        {
            token.Value.DangerousAddRef(ref addedRef);
            Guid folderId = WindowsKnownFolderPath.SkyDrive;
            nint pathPointer = 0;
            int hresult = GetKnownFolderPath(
                in folderId,
                KnownFolderFlagsDefault,
                token.Value.DangerousGetHandle(),
                ref pathPointer);
            try
            {
                string? path = pathPointer == 0
                    ? null
                    : CopyKnownFolderPath(pathPointer);
                return InterpretSkyDriveLookup(hresult, path);
            }
            finally
            {
                if (pathPointer != 0)
                {
                    FreeKnownFolderPath(pathPointer);
                }
            }
        }
        finally
        {
            if (addedRef)
            {
                token.Value.DangerousRelease();
            }
        }
    }

    internal virtual Result<Guid[]> GetRegisteredKnownFolderIds()
    {
        object? managerObject = null;
        nint identifiers = 0;
        try
        {
            managerObject = new KnownFolderManagerComClass();
            var manager = (IKnownFolderManager)managerObject;
            int hresult = manager.GetFolderIds(out identifiers, out uint count);
            if (hresult != 0
                || count > (uint)int.MaxValue
                || (count > 0 && identifiers == 0))
            {
                return Failure<Guid[]>();
            }

            var result = new Guid[checked((int)count)];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = Marshal.PtrToStructure<Guid>(
                    identifiers + checked(index * Marshal.SizeOf<Guid>()));
            }

            return Result<Guid[]>.Success(result);
        }
        catch (Exception)
        {
            return Failure<Guid[]>();
        }
        finally
        {
            if (identifiers != 0)
            {
                Marshal.FreeCoTaskMem(identifiers);
            }

            if (managerObject is not null && Marshal.IsComObject(managerObject))
            {
                _ = Marshal.FinalReleaseComObject(managerObject);
            }
        }
    }

    internal virtual int GetKnownFolderPath(
        in Guid folderId,
        uint flags,
        nint token,
        ref nint pathPointer) =>
        NativeMethods.SHGetKnownFolderPath(
            in folderId,
            flags,
            token,
            out pathPointer);

    internal virtual string? CopyKnownFolderPath(nint pathPointer) =>
        Marshal.PtrToStringUni(pathPointer);

    internal virtual void FreeKnownFolderPath(nint pathPointer) =>
        Marshal.FreeCoTaskMem(pathPointer);

    internal virtual Result<SafeFileHandle> OpenDirectory(string path) =>
        _paths.OpenPath(path, NativeMethods.FileReadAttributes);

    internal virtual Result<NativeMethods.FileAttributeTagInfo> GetAttributes(
        SafeFileHandle handle) => _paths.GetAttributeTagInfo(handle);

    internal virtual Result<string> GetFinalPath(SafeFileHandle handle) =>
        _paths.GetFinalPath(handle);

    internal virtual Result<DirectoryIdentity> GetIdentity(SafeFileHandle handle) =>
        _paths.GetDirectoryIdentity(handle);

    internal static bool IsNotUnderSyncRoot(int hresult) =>
        hresult is HResultFromWin32CloudFileNotUnderSyncRoot
            or HResultFromNtCloudFileNotUnderSyncRoot;

    internal static Result<KnownFolderLookup> InterpretSkyDriveLookup(
        int hresult,
        string? path)
    {
        if (hresult is HResultFileNotFound or HResultPathNotFound)
        {
            return Result<KnownFolderLookup>.Success(new(false, null));
        }

        if (hresult != 0
            || string.IsNullOrEmpty(path)
            || !Path.IsPathFullyQualified(path))
        {
            return Failure<KnownFolderLookup>();
        }

        return Result<KnownFolderLookup>.Success(new(true, Path.GetFullPath(path)));
    }

    private static Result<T> Failure<T>() => Result<T>.Failure(new Error(
        BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
        BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
        ErrorCategory.UnrecoverableError));

    [ComImport]
    [Guid("4DF0C730-DF9D-4AE3-9153-AA6B82E9795A")]
    private sealed class KnownFolderManagerComClass
    {
    }

    [ComImport]
    [Guid("8BE2D872-86AA-4D47-B776-32CCA40C7018")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKnownFolderManager
    {
        [PreserveSig]
        int FolderIdFromCsidl(int csidl, out Guid folderId);

        [PreserveSig]
        int FolderIdToCsidl(in Guid folderId, out int csidl);

        [PreserveSig]
        int GetFolderIds(out nint folderIds, out uint count);
    }

    [DllImport("cldapi.dll")]
    private static extern int CfGetSyncRootInfoByHandle(
        SafeFileHandle fileHandle,
        int infoClass,
        nint infoBuffer,
        int infoBufferLength,
        out int returnedLength);

}
