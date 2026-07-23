namespace FolderSessionLock.Broker.Security;

public enum BrokerRunMode
{
    ConsentBroker,
    RecoveryService,
    RecoveryOnce,
}

public sealed record BrokerStartupOptions(
    BrokerRunMode RunMode,
    BrokerConsentOptions? ConsentOptions)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out BrokerStartupOptions? options)
    {
        options = null;
        if (arguments.Count == 2
            && arguments[0] == "--mode")
        {
            if (arguments[1] == "recovery-service")
            {
                options = new BrokerStartupOptions(BrokerRunMode.RecoveryService, null);
                return true;
            }

            if (arguments[1] == "recovery-once")
            {
                options = new BrokerStartupOptions(BrokerRunMode.RecoveryOnce, null);
                return true;
            }

            return false;
        }

        if (!BrokerConsentOptions.TryParse(arguments, out BrokerConsentOptions? consentOptions))
        {
            return false;
        }

        options = new BrokerStartupOptions(BrokerRunMode.ConsentBroker, consentOptions);
        return true;
    }
}
