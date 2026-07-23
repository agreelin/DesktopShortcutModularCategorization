using System.Security.AccessControl;

namespace FolderSessionLock.Windows.Security;

public static class FolderDenyAccessMask
{
    public const FileSystemRights Value =
        FileSystemRights.ListDirectory
        | FileSystemRights.CreateFiles
        | FileSystemRights.CreateDirectories
        | FileSystemRights.ReadExtendedAttributes
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.Traverse
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ReadAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete;
}
