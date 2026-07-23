using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Recovery;

internal sealed class RecoveryCreateLockGate
{
    private readonly IRecoveryReadinessReader _reader;
    private readonly IRecoveryStoreWriteSafetyState _writeSafetyState;

    internal RecoveryCreateLockGate(
        IRecoveryReadinessReader reader,
        IRecoveryStoreWriteSafetyState? writeSafetyState = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writeSafetyState = writeSafetyState ?? new RecoveryStoreWriteSafetyState();
    }

    internal async ValueTask<BrokerError?> CheckAsync(CancellationToken cancellationToken)
    {
        if (_writeSafetyState.IsWriteBlocked)
        {
            return BlockingError();
        }

        try
        {
            RecoveryReadinessSnapshot snapshot = await _reader.ReadAsync(cancellationToken);
            return RecoveryReadinessPolicy.IsReady(snapshot, DateTimeOffset.UtcNow)
                ? null
                : BlockingError();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return BlockingError();
        }
    }

    internal static BrokerError BlockingError() => new(
        BrokerErrorCodes.FSL_E_RECOVERY_BLOCKING,
        "Folder restrictions cannot be created until recovery is complete.",
        true,
        null);
}

internal sealed class UnavailableRecoveryReadinessReader : IRecoveryReadinessReader
{
    public ValueTask<RecoveryReadinessSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<RecoveryReadinessSnapshot>(
            new InvalidOperationException(BrokerErrorCodes.FSL_E_RECOVERY_BLOCKING));
}
