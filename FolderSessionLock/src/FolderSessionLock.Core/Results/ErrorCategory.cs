namespace FolderSessionLock.Core.Results;

public enum ErrorCategory
{
    ValidationFailed,
    InsufficientPermissions,
    UnsupportedPath,
    PlatformError,
    RecoverableError,
    UnrecoverableError,
}
