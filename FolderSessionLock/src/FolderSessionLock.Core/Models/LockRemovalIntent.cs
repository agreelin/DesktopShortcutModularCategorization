namespace FolderSessionLock.Core.Models;

public enum LockRemovalIntent
{
    Expiration,
    Recovery,
    TestCleanup,
    AdministrativeCleanup,
}
