using System.Runtime.InteropServices;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Interop;

namespace FolderSessionLock.Windows.Services;

public sealed class FolderPathSafetyPolicy
{
    private static readonly Error ProtectedPathError = new(
        "windows.path.protected",
        "The directory is inside a protected path.",
        ErrorCategory.UnsupportedPath);

    private readonly string _userProfileRoot;
    private readonly string[] _protectedTrees;

    public FolderPathSafetyPolicy(string installationRoot)
        : this(installationRoot, SystemPathRoots.ReadCurrent())
    {
    }

    internal FolderPathSafetyPolicy(
        string repositoryRoot,
        string installationRoot,
        IEnumerable<string> synchronizationRoots)
        : this(
            repositoryRoot,
            installationRoot,
            synchronizationRoots,
            SystemPathRoots.ReadCurrent())
    {
    }

    private FolderPathSafetyPolicy(
        string installationRoot,
        SystemPathRoots systemRoots)
    {
        ArgumentNullException.ThrowIfNull(systemRoots);
        _userProfileRoot = NormalizeRequired(systemRoots.UserProfileRoot);
        _protectedTrees =
        [
            NormalizeRequired(systemRoots.DesktopRoot),
            NormalizeRequired(systemRoots.DocumentsRoot),
            NormalizeRequired(systemRoots.DownloadsRoot),
            NormalizeRequired(systemRoots.WindowsRoot),
            NormalizeRequired(systemRoots.SystemRoot),
            .. systemRoots.ProgramFilesRoots.Select(NormalizeRequired),
            NormalizeRequired(systemRoots.ProgramDataRoot),
            NormalizeRequired(installationRoot),
        ];
    }

    internal FolderPathSafetyPolicy(
        string repositoryRoot,
        string installationRoot,
        IEnumerable<string> synchronizationRoots,
        SystemPathRoots systemRoots)
    {
        ArgumentNullException.ThrowIfNull(synchronizationRoots);
        ArgumentNullException.ThrowIfNull(systemRoots);

        _userProfileRoot = NormalizeRequired(systemRoots.UserProfileRoot);
        _protectedTrees =
        [
            NormalizeRequired(systemRoots.DesktopRoot),
            NormalizeRequired(systemRoots.DocumentsRoot),
            NormalizeRequired(systemRoots.DownloadsRoot),
            NormalizeRequired(systemRoots.WindowsRoot),
            NormalizeRequired(systemRoots.SystemRoot),
            .. systemRoots.ProgramFilesRoots.Select(NormalizeRequired),
            NormalizeRequired(systemRoots.ProgramDataRoot),
            NormalizeRequired(repositoryRoot),
            NormalizeRequired(installationRoot),
            .. synchronizationRoots.Select(NormalizeRequired),
        ];
    }

    public Result Validate(string path)
    {
        string normalizedPath = NormalizeRequired(path);
        if (string.Equals(normalizedPath, _userProfileRoot, StringComparison.OrdinalIgnoreCase)
            || _protectedTrees.Any(root => IsSameOrDescendant(root, normalizedPath)))
        {
            return Result.Failure(ProtectedPathError);
        }

        return Result.Success();
    }

    private static bool IsSameOrDescendant(string root, string path)
    {
        string rootPrefix = Path.GetPathRoot(root)!;
        string pathPrefix = Path.GetPathRoot(path)!;
        if (!string.Equals(rootPrefix, pathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] rootComponents = SplitComponents(root, rootPrefix);
        string[] pathComponents = SplitComponents(path, pathPrefix);
        return rootComponents.Length <= pathComponents.Length
            && rootComponents.SequenceEqual(
                pathComponents.Take(rootComponents.Length),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string[] SplitComponents(string path, string root) =>
        path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeRequired(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A safety policy root must be fully qualified.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}

internal sealed record SystemPathRoots(
    string UserProfileRoot,
    string DesktopRoot,
    string DocumentsRoot,
    string DownloadsRoot,
    string WindowsRoot,
    string SystemRoot,
    IReadOnlyList<string> ProgramFilesRoots,
    string ProgramDataRoot)
{
    private static readonly Guid DownloadsFolderId = new(
        0x374de290,
        0x123f,
        0x4565,
        0x91,
        0x64,
        0x39,
        0xc4,
        0x92,
        0x5e,
        0x46,
        0x7b);

    internal static SystemPathRoots ReadCurrent()
    {
        string[] programFilesRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        return new SystemPathRoots(
            RequireEnvironmentPath(Environment.SpecialFolder.UserProfile),
            RequireEnvironmentPath(Environment.SpecialFolder.DesktopDirectory),
            RequireEnvironmentPath(Environment.SpecialFolder.MyDocuments),
            GetDownloadsPath(),
            RequireEnvironmentPath(Environment.SpecialFolder.Windows),
            RequireEnvironmentPath(Environment.SpecialFolder.System),
            programFilesRoots.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
            RequireEnvironmentPath(Environment.SpecialFolder.CommonApplicationData));
    }

    private static string RequireEnvironmentPath(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        return !string.IsNullOrWhiteSpace(path)
            ? path
            : throw new InvalidOperationException($"Windows did not return the {folder} path.");
    }

    private static string GetDownloadsPath()
    {
        int result = NativeMethods.SHGetKnownFolderPath(
            in DownloadsFolderId,
            0,
            nint.Zero,
            out nint pathPointer);
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        try
        {
            return Marshal.PtrToStringUni(pathPointer)
                ?? throw new InvalidOperationException("Windows returned an empty Downloads path.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }
}
