namespace FolderSessionLock.Core.Recovery;

public enum RecoveryReadinessState
{
    Starting,
    Ready,
    RecoveryBlocked,
    Stopping,
}

public sealed record RecoveryReadinessSnapshot(
    int SchemaVersion,
    string ServiceName,
    Guid ServiceInstanceId,
    long Sequence,
    RecoveryReadinessState State,
    bool RecoveryBlocking,
    DateTimeOffset ScanStartedUtc,
    DateTimeOffset? ScanCompletedUtc,
    DateTimeOffset PublishedUtc,
    DateTimeOffset ValidUntilUtc,
    int RemainingRecordCount,
    string? PrimaryErrorCode);

public interface IRecoveryReadinessPublisher
{
    ValueTask PublishAsync(
        RecoveryReadinessSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface IRecoveryReadinessReader
{
    ValueTask<RecoveryReadinessSnapshot> ReadAsync(
        CancellationToken cancellationToken);
}

public interface IRecoveryReadinessStore : IRecoveryReadinessPublisher, IRecoveryReadinessReader
{
    ValueTask DeleteAsync(CancellationToken cancellationToken);
}

public sealed class RecoveryReadinessException : Exception
{
    public RecoveryReadinessException(string code)
        : base(code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public RecoveryReadinessException(string code, Exception innerException)
        : base(code, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
