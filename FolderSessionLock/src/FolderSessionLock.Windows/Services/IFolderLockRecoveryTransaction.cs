using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Windows.Services;

public interface IFolderLockRecoveryTransaction
{
    ValueTask<Result<Guid>> PrepareAsync(
        FolderLockRequest request,
        SessionIdentity sessionIdentity,
        ValidatedDirectory directory,
        RecoveryAclEvidence evidence,
        CancellationToken cancellationToken);

    ValueTask<Result> MarkAppliedAsync(
        Guid recoveryRecordId,
        RecoveryAclEvidence evidence,
        CancellationToken cancellationToken);

    ValueTask<Result> MarkCleanupPendingAsync(
        Guid recoveryRecordId,
        CancellationToken cancellationToken);

    ValueTask<Result> MarkCleanupFailedAsync(
        Guid recoveryRecordId,
        Error error,
        CancellationToken cancellationToken);

    ValueTask<Result> DeleteAsync(
        Guid recoveryRecordId,
        CancellationToken cancellationToken);
}

internal sealed class InMemoryFolderLockRecoveryTransaction : IFolderLockRecoveryTransaction
{
    internal static InMemoryFolderLockRecoveryTransaction Instance { get; } = new();

    public ValueTask<Result<Guid>> PrepareAsync(
        FolderLockRequest request,
        SessionIdentity sessionIdentity,
        ValidatedDirectory directory,
        RecoveryAclEvidence evidence,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<Guid>.Success(Guid.NewGuid()));

    public ValueTask<Result> MarkAppliedAsync(
        Guid recoveryRecordId,
        RecoveryAclEvidence evidence,
        CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> MarkCleanupPendingAsync(
        Guid recoveryRecordId,
        CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> MarkCleanupFailedAsync(
        Guid recoveryRecordId,
        Error error,
        CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

    public ValueTask<Result> DeleteAsync(
        Guid recoveryRecordId,
        CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
}
