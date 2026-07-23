namespace FolderSessionLock.Protocol;

public enum ConsentBrokerExitCode
{
    ProtocolHandledOrLifecycleCompleted = 0,
    InvalidArguments = 2,
    CrossAccountElevationNotSupported = 20,
    InitiatingClientIdentityUnavailable = 21,
    InitiatingClientProcessMismatch = 22,
    PipeInitializationFailed = 23,
    ClientConnectTimeout = 24,
    ProtocolFailedBeforeResponse = 25,
    ResponseWriteFailed = 26,
    LifecycleCleanupFailed = 27,
    ProtectedLoggerUnavailableOrInternalFailure = 28,
    LauncherTerminatedBeforeConnect = 29,
}
