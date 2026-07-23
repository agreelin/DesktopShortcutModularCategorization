using System.Runtime.InteropServices;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Interop;

namespace FolderSessionLock.Windows.Services;

internal static class WindowsKnownFolderPath
{
    internal static readonly Guid ProgramData = new("62ab5d82-fdc1-4dc3-a9dd-070d1d495d97");
    internal static readonly Guid ProgramFiles = new("905e63b6-c1bf-494e-b29c-65b732d3d21a");
    internal static readonly Guid SkyDrive = new("a52bba46-e9e1-435f-b3d9-28daa648c0f6");

    internal static Result<string> GetPath(Guid folderId, nint token = default)
    {
        int hresult = NativeMethods.SHGetKnownFolderPath(
            in folderId,
            0,
            token,
            out nint pathPointer);
        if (hresult < 0)
        {
            return Result<string>.Failure(new Error(
                "windows.known_folder.unavailable",
                "The required Windows known folder is unavailable.",
                ErrorCategory.PlatformError));
        }

        try
        {
            string? path = Marshal.PtrToStringUni(pathPointer);
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                ? Result<string>.Success(Path.GetFullPath(path))
                : Result<string>.Failure(new Error(
                    "windows.known_folder.unavailable",
                    "The required Windows known folder is unavailable.",
                    ErrorCategory.PlatformError));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    internal static string GetRequiredPath(Guid folderId)
    {
        Result<string> result = GetPath(folderId);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(result.Error!.Code);
    }
}
