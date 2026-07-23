namespace FolderSessionLock.Broker.Logging;

public static class ProtectedLogEventCatalog
{
    public static readonly ProtectedLogEvent BrokerStarting = new(
        1001,
        "BrokerStarting",
        ProtectedLogComponent.BrokerBootstrap,
        "The elevated broker is starting.");

    public static readonly ProtectedLogEvent BrokerBootstrapFailed = new(
        1002,
        "BrokerBootstrapFailed",
        ProtectedLogComponent.BrokerBootstrap,
        "The elevated broker bootstrap failed.");

    public static readonly ProtectedLogEvent ReadinessStateChanged = new(
        2001,
        "ReadinessStateChanged",
        ProtectedLogComponent.Readiness,
        "The recovery readiness state changed.");

    public static readonly ProtectedLogEvent SchedulerStopped = new(
        3001,
        "SchedulerStopped",
        ProtectedLogComponent.Scheduler,
        "The lock task scheduler loop terminated unexpectedly.");

    public static readonly ProtectedLogEvent LifecycleCleanupFailed = new(
        4001,
        "LifecycleCleanupFailed",
        ProtectedLogComponent.Lifecycle,
        "The broker lifecycle cleanup failed.");

    public static readonly ProtectedLogEvent LifecycleCleanupTaskFailed = new(
        4002,
        "LifecycleCleanupTaskFailed",
        ProtectedLogComponent.Lifecycle,
        "A broker lifecycle cleanup task failed.");

    public static readonly ProtectedLogEvent LifecycleCleanupCompleted = new(
        4003,
        "LifecycleCleanupCompleted",
        ProtectedLogComponent.Lifecycle,
        "The broker lifecycle cleanup completed.");

    public static readonly ProtectedLogEvent LifecycleCleanupRecoveryRequired = new(
        4004,
        "LifecycleCleanupRecoveryRequired",
        ProtectedLogComponent.Lifecycle,
        "The broker lifecycle cleanup requires recovery.");

    public static readonly ProtectedLogEvent LoggerFailed = new(
        5001,
        "LoggerFailed",
        ProtectedLogComponent.Logger,
        "The protected diagnostic logger failed.");

    private static readonly IReadOnlyDictionary<int, ProtectedLogEvent> Events =
        new[]
        {
            BrokerStarting,
            BrokerBootstrapFailed,
            ReadinessStateChanged,
            SchedulerStopped,
            LifecycleCleanupFailed,
            LifecycleCleanupTaskFailed,
            LifecycleCleanupCompleted,
            LifecycleCleanupRecoveryRequired,
            LoggerFailed,
        }.ToDictionary(item => item.EventId);

    public static bool TryGet(int eventId, out ProtectedLogEvent? entry) =>
        Events.TryGetValue(eventId, out entry);
}
