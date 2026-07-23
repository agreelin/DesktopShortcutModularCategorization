using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Protocol;

public enum BrokerExecutionContext
{
    OrdinaryUi,
    ConsentBrokerInternalScheduler,
    RecoveryService,
    RecoveryOnce,
    TestCleanup,
}

public sealed record BrokerPermissionDecision(
    bool IsAllowed,
    LockRemovalIntent? RemovalIntent,
    BrokerError? Error);

public static class BrokerPermissionPolicy
{
    public static BrokerPermissionDecision Evaluate(
        BrokerExecutionContext executionContext,
        BrokerCommand command)
    {
        if (!Enum.IsDefined(executionContext) || !Enum.IsDefined(command))
        {
            return Denied();
        }

        if (command == BrokerCommand.RemoveLock)
        {
            return executionContext switch
            {
                BrokerExecutionContext.ConsentBrokerInternalScheduler => Allowed(LockRemovalIntent.Expiration),
                BrokerExecutionContext.RecoveryService => Allowed(LockRemovalIntent.Recovery),
                BrokerExecutionContext.RecoveryOnce => Allowed(LockRemovalIntent.Recovery),
                BrokerExecutionContext.TestCleanup => Allowed(LockRemovalIntent.TestCleanup),
                _ => Denied(),
            };
        }

        bool allowed = executionContext switch
        {
            BrokerExecutionContext.OrdinaryUi => command is
                BrokerCommand.ValidatePath or BrokerCommand.CreateLock or BrokerCommand.GetStatus,
            BrokerExecutionContext.ConsentBrokerInternalScheduler => true,
            BrokerExecutionContext.RecoveryService or BrokerExecutionContext.RecoveryOnce => command is
                BrokerCommand.ValidatePath or BrokerCommand.GetStatus,
            BrokerExecutionContext.TestCleanup => false,
            _ => false,
        };

        return allowed ? Allowed(null) : Denied();
    }

    private static BrokerPermissionDecision Allowed(LockRemovalIntent? removalIntent) =>
        new(true, removalIntent, null);

    private static BrokerPermissionDecision Denied() => new(
        false,
        null,
        new BrokerError(
            BrokerErrorCodes.FSL_E_UNAUTHORIZED_CALLER,
            "The caller is not authorized for this operation.",
            false,
            null));
}
