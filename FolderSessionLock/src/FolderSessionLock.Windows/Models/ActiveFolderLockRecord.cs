using FolderSessionLock.Core.Models;
using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Windows.Models;

internal sealed record ActiveFolderLockRecord(
    Guid TaskId,
    FolderPath FolderPath,
    TimeSpan Duration,
    SessionIdentity SessionIdentity,
    DirectoryIdentity Identity,
    Guid RecoveryRecordId,
    ValidatedDirectory Directory,
    DirectoryAclOperation? AclOperation);
