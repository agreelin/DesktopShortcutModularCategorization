using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Models;

public sealed class ValidatedDirectory : IDisposable
{
    internal ValidatedDirectory(
        string normalizedPath,
        string finalPath,
        DirectoryIdentity identity,
        SafeFileHandle handle,
        bool hasReadControl,
        bool hasWriteDac)
    {
        NormalizedPath = normalizedPath;
        FinalPath = finalPath;
        Identity = identity;
        Handle = handle;
        HasReadControl = hasReadControl;
        HasWriteDac = hasWriteDac;
    }

    public string NormalizedPath { get; }

    public string FinalPath { get; }

    public DirectoryIdentity Identity { get; }

    public SafeFileHandle Handle { get; }

    public bool HasReadControl { get; }

    public bool HasWriteDac { get; }

    public void Dispose() => Handle.Dispose();
}
