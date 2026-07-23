using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.Broker.Logging;

public sealed class ProtectedJsonLinesLoggerProvider : ILoggerProvider
{
    internal const int MaximumLineBytes = 4096;
    internal const long MaximumFileBytes = 8 * 1024 * 1024;
    internal const int MaximumRotationIndex = 9999;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _writeGate = new();
    private readonly ProtectedLoggerMode _mode;
    private readonly Guid _instanceId;
    private readonly IProtectedLogFilePlatform _platform;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly uint _processId;
    private readonly DateTimeOffset _startedUtc;
    private readonly long _maximumFileBytes;
    private readonly Func<Result>? _beforeFileCreate;
    private IProtectedLogFile? _file;
    private DateOnly _fileUtcDate;
    private long _fileLength;
    private long _sequence;
    private int _rotationIndex = -1;
    private bool _disposed;

    internal ProtectedJsonLinesLoggerProvider(
        ProtectedLoggerMode mode,
        Guid instanceId,
        IProtectedLogFilePlatform platform,
        Func<DateTimeOffset>? utcNow = null,
        uint? processId = null,
        long maximumFileBytes = MaximumFileBytes,
        Func<Result>? beforeFileCreate = null)
    {
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("The logger instance ID must not be empty.", nameof(instanceId));
        }

        if (maximumFileBytes <= 0 || maximumFileBytes > MaximumFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        _mode = mode;
        _instanceId = instanceId;
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _processId = processId ?? checked((uint)Environment.ProcessId);
        _startedUtc = _utcNow().ToUniversalTime();
        _maximumFileBytes = maximumFileBytes;
        _beforeFileCreate = beforeFileCreate;
    }

    internal bool IsPermanentlyFailed { get; private set; }

    internal string? CurrentLeafName => _file?.LeafName;

    internal Result Initialize()
    {
        lock (_writeGate)
        {
            return OpenNextFile(_startedUtc);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return new ProtectedJsonLinesLogger(this);
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _file?.Dispose();
            _file = null;
        }
    }

    private bool IsEnabled(LogLevel logLevel) =>
        !_disposed
        && !IsPermanentlyFailed
        && logLevel is LogLevel.Information
            or LogLevel.Warning
            or LogLevel.Error
            or LogLevel.Critical;

    private void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state)
    {
        if (!IsEnabled(logLevel)
            || !ProtectedLogEventCatalog.TryGet(eventId.Id, out ProtectedLogEvent? catalogEvent))
        {
            return;
        }

        ProtectedLogContext context = state is ProtectedLogContext protectedContext
            ? protectedContext
            : new ProtectedLogContext();
        lock (_writeGate)
        {
            if (!IsEnabled(logLevel) || _file is null)
            {
                return;
            }

            DateTimeOffset timestamp = _utcNow().ToUniversalTime();
            byte[] line;
            try
            {
                ValidateContext(context);
                line = Serialize(timestamp, logLevel, catalogEvent!, context, _sequence);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or EncoderFallbackException
                    or InvalidOperationException)
            {
                FailPermanently();
                return;
            }

            if (line.Length > MaximumLineBytes)
            {
                FailPermanently();
                return;
            }

            if (_fileLength + line.Length > _maximumFileBytes
                || DateOnly.FromDateTime(timestamp.UtcDateTime) != _fileUtcDate)
            {
                Result rotation = OpenNextFile(timestamp);
                if (rotation.IsFailure)
                {
                    return;
                }

                line = Serialize(timestamp, logLevel, catalogEvent!, context, _sequence);
                if (line.Length > MaximumLineBytes || line.Length > _maximumFileBytes)
                {
                    FailPermanently();
                    return;
                }
            }

            Result write = _platform.Write(_file, line, _fileLength);
            if (write.IsFailure || _platform.Flush(_file).IsFailure)
            {
                FailPermanently();
                return;
            }

            _fileLength += line.Length;
            _sequence++;
        }
    }

    private Result OpenNextFile(DateTimeOffset fileUtc)
    {
        if (_disposed || IsPermanentlyFailed || _rotationIndex >= MaximumRotationIndex)
        {
            return Failure();
        }

        int nextIndex = _rotationIndex + 1;
        if (_beforeFileCreate?.Invoke().IsFailure == true)
        {
            FailPermanently();
            return Failure();
        }

        string leafName = string.Create(
            CultureInfo.InvariantCulture,
            $"{_startedUtc:yyyyMMdd'T'HHmmssfffffff'Z'}-{_processId}-{_instanceId:D}-{nextIndex:0000}.jsonl");
        Result<IProtectedLogFile> create = _platform.CreateNew(_mode, leafName);
        if (create.IsFailure)
        {
            FailPermanently();
            return Failure();
        }

        _file?.Dispose();
        _file = create.Value;
        _rotationIndex = nextIndex;
        _fileUtcDate = DateOnly.FromDateTime(fileUtc.UtcDateTime);
        _fileLength = 0;
        _sequence = 1;
        return Result.Success();
    }

    private byte[] Serialize(
        DateTimeOffset timestamp,
        LogLevel level,
        ProtectedLogEvent catalogEvent,
        ProtectedLogContext context,
        long sequence)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString(
                "timestampUtc",
                timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteNumber("sequence", sequence);
            writer.WriteString("level", level.ToString());
            writer.WriteNumber("eventId", catalogEvent.EventId);
            writer.WriteString("eventName", catalogEvent.EventName);
            writer.WriteString("mode", _mode.ToString());
            writer.WriteString("component", catalogEvent.Component.ToString());
            writer.WriteNumber("processId", _processId);
            writer.WriteString("instanceId", LowerGuid(_instanceId));
            WriteGuidOrNull(writer, "requestId", context.RequestId);
            WriteGuidOrNull(writer, "taskId", context.TaskId);
            if (context.ErrorCode is null)
            {
                writer.WriteNull("errorCode");
            }
            else
            {
                writer.WriteString("errorCode", context.ErrorCode);
            }

            writer.WriteString("message", catalogEvent.Message);
            writer.WriteEndObject();
            writer.Flush();
        }

        var line = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(line);
        line[^1] = (byte)'\n';
        _ = Utf8.GetString(line.AsSpan(0, line.Length - 1));
        return line;
    }

    private static void ValidateContext(ProtectedLogContext context)
    {
        if (context.RequestId == Guid.Empty || context.TaskId == Guid.Empty)
        {
            throw new ArgumentException("Protected log identifiers must not be empty.");
        }

        if (context.ErrorCode is not null
            && (context.ErrorCode.Length > 128
                || (!context.ErrorCode.StartsWith("FSL_E_", StringComparison.Ordinal)
                    && !context.ErrorCode.StartsWith("lock_task.", StringComparison.Ordinal))))
        {
            throw new ArgumentException("The protected log error code is invalid.");
        }
    }

    private static void WriteGuidOrNull(Utf8JsonWriter writer, string propertyName, Guid? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, LowerGuid(value.Value));
        }
    }

    private static string LowerGuid(Guid value) => value.ToString("D").ToLowerInvariant();

    private void FailPermanently()
    {
        IsPermanentlyFailed = true;
        _file?.Dispose();
        _file = null;
    }

    internal void FailMaintenance() => FailPermanently();

    private static Result Failure() => Result.Failure(new Error(
        BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
        "The protected diagnostic logger could not be initialized.",
        ErrorCategory.UnrecoverableError));

    private sealed class ProtectedJsonLinesLogger(ProtectedJsonLinesLoggerProvider provider)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            provider.Log(logLevel, eventId, state);
    }
}

internal sealed class ProtectedLoggerFactory(
    ProtectedJsonLinesLoggerProvider provider,
    ProtectedLogRetention? retention = null,
    Func<DateTimeOffset>? utcNow = null) : ILoggerFactory, IProtectedLoggerHealth, IProtectedLogMaintenance
{
    public TimeSpan MaintenanceInterval => TimeSpan.FromHours(24);

    public bool IsPermanentlyFailed => provider.IsPermanentlyFailed;

    public Result RunMaintenance()
    {
        if (retention is null)
        {
            return Result.Success();
        }

        Result result = retention.Cleanup((utcNow ?? (() => DateTimeOffset.UtcNow))());
        if (result.IsFailure)
        {
            provider.FailMaintenance();
        }

        return result;
    }

    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("The protected logger factory does not accept providers.");

    public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

    public void Dispose() => provider.Dispose();
}
