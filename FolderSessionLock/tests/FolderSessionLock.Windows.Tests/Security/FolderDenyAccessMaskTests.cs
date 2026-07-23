using System.Security.AccessControl;
using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Windows.Tests.Security;

public sealed class FolderDenyAccessMaskTests
{
    [Fact]
    public void Value_IsExactRequiredMask()
    {
        Assert.Equal(0x000101FF, (int)FolderDenyAccessMask.Value);
    }

    [Fact]
    public void Value_ContainsEveryRequiredRight()
    {
        FileSystemRights[] rights =
        [
            FileSystemRights.ListDirectory,
            FileSystemRights.CreateFiles,
            FileSystemRights.CreateDirectories,
            FileSystemRights.ReadExtendedAttributes,
            FileSystemRights.WriteExtendedAttributes,
            FileSystemRights.Traverse,
            FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.ReadAttributes,
            FileSystemRights.WriteAttributes,
            FileSystemRights.Delete,
        ];

        foreach (FileSystemRights right in rights)
        {
            Assert.Equal(right, FolderDenyAccessMask.Value & right);
        }
    }

    [Fact]
    public void Value_DoesNotDenyRecoveryRights()
    {
        FileSystemRights recoveryRights = FileSystemRights.ReadPermissions
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership
            | FileSystemRights.Synchronize;

        Assert.Equal(0, (int)(FolderDenyAccessMask.Value & recoveryRights));
        Assert.NotEqual(FileSystemRights.FullControl, FolderDenyAccessMask.Value);
        Assert.NotEqual(FileSystemRights.Modify, FolderDenyAccessMask.Value);
        Assert.NotEqual(FileSystemRights.Write, FolderDenyAccessMask.Value);
    }
}
