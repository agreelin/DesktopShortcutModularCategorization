namespace FolderSessionLock.Broker.Recovery;

internal interface IRecoveryStoreWriteSafetyState
{
    bool IsWriteBlocked { get; }

    string? BlockingErrorCode { get; }

    void BlockWrites(string errorCode);
}

internal sealed class RecoveryStoreWriteSafetyState : IRecoveryStoreWriteSafetyState
{
    private int _blocked;
    private string? _errorCode;

    public bool IsWriteBlocked => Volatile.Read(ref _blocked) != 0;

    public string? BlockingErrorCode => Volatile.Read(ref _errorCode);

    public void BlockWrites(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        Interlocked.CompareExchange(ref _errorCode, errorCode, null);
        Interlocked.Exchange(ref _blocked, 1);
    }
}
