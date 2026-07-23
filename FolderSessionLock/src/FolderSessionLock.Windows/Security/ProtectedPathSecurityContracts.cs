namespace FolderSessionLock.Windows.Security;

public enum ProtectedPathKind
{
    InstallDirectory,
    RecoveryRoot,
    RecoveryRecordsDirectory,
    ReplayDirectory
}

public sealed record ProtectedPathSecurityCheckRequest(
    ProtectedPathKind PathKind,
    string ExpectedPath);

public sealed record ProtectedPathSecurityCheckResult(
    bool IsTrusted,
    string? ErrorCode);

public interface IProtectedPathSecurityVerifier
{
    ValueTask<ProtectedPathSecurityCheckResult> VerifyAsync(
        ProtectedPathSecurityCheckRequest request,
        CancellationToken cancellationToken);
}
