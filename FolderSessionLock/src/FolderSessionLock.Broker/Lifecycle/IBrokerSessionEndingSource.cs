namespace FolderSessionLock.Broker.Lifecycle;

internal interface IBrokerSessionEndingSource
{
    ValueTask WaitAsync(CancellationToken cancellationToken = default);
}
