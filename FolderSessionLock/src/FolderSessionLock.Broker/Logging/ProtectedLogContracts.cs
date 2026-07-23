using FolderSessionLock.Core.Results;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.Broker.Logging;

public enum ProtectedLoggerMode
{
    ConsentBroker,
    RecoveryService,
    RecoveryOnce,
}

public enum ProtectedLogComponent
{
    BrokerBootstrap,
    Elevation,
    Transport,
    Protocol,
    Replay,
    Recovery,
    Scheduler,
    Lifecycle,
    Readiness,
    Security,
    Logger,
}

public sealed record ProtectedLogContext(
    Guid? RequestId = null,
    Guid? TaskId = null,
    string? ErrorCode = null);

public sealed record ProtectedLogEvent(
    int EventId,
    string EventName,
    ProtectedLogComponent Component,
    string Message);

public interface IProtectedLoggerFactory
{
    Result<ILoggerFactory> Create(ProtectedLoggerMode mode, Guid instanceId);
}

internal interface IProtectedLoggerHealth
{
    bool IsPermanentlyFailed { get; }
}

internal interface IProtectedLogMaintenance
{
    TimeSpan MaintenanceInterval { get; }

    Result RunMaintenance();
}

internal interface IProtectedLogFile : IDisposable
{
    string LeafName { get; }
}

internal interface IProtectedLogFilePlatform
{
    Result<IProtectedLogFile> CreateNew(
        ProtectedLoggerMode mode,
        string leafName);

    Result Write(
        IProtectedLogFile file,
        ReadOnlyMemory<byte> bytes,
        long offset);

    Result Flush(IProtectedLogFile file);
}
