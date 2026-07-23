using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Transport;

public enum BrokerExecutionEffect
{
    Succeeded,
    FailedWithoutSideEffects,
    RolledBack,
    RecoveryRequired,
}

public sealed class BrokerExecutionOutcome
{
    private BrokerExecutionOutcome(BrokerResponseEnvelope response, BrokerExecutionEffect effect)
    {
        Response = response;
        Effect = effect;
    }

    public BrokerResponseEnvelope Response { get; }

    public BrokerExecutionEffect Effect { get; }

    public static BrokerExecutionOutcome Succeeded(BrokerResponseEnvelope response) =>
        response is not null && response.Success
            ? new BrokerExecutionOutcome(response, BrokerExecutionEffect.Succeeded)
            : throw new ArgumentException("A successful execution requires a successful response.", nameof(response));

    public static BrokerExecutionOutcome FailedWithoutSideEffects(BrokerResponseEnvelope response) =>
        Failed(response, BrokerExecutionEffect.FailedWithoutSideEffects);

    public static BrokerExecutionOutcome RolledBack(BrokerResponseEnvelope response) =>
        Failed(response, BrokerExecutionEffect.RolledBack);

    public static BrokerExecutionOutcome RecoveryRequired(BrokerResponseEnvelope response) =>
        Failed(response, BrokerExecutionEffect.RecoveryRequired);

    private static BrokerExecutionOutcome Failed(
        BrokerResponseEnvelope response,
        BrokerExecutionEffect effect) =>
        response is not null && !response.Success && response.Error is not null
            ? new BrokerExecutionOutcome(response, effect)
            : throw new ArgumentException("A failed execution requires a failed response.", nameof(response));
}
