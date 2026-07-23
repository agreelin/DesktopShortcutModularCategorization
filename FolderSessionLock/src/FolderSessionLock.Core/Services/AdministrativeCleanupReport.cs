using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Services;

public sealed record AdministrativeCleanupFailure(
    Guid TaskId,
    string ErrorCode,
    bool IsFirstError,
    bool RecoveryRequired);

public sealed record AdministrativeCleanupReport(
    Result<int> Result,
    IReadOnlyList<AdministrativeCleanupFailure> Failures,
    bool FullyTraversed,
    bool RecoveryRequired);
