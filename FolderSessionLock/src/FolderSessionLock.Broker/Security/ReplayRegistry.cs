using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Security;

public enum ReplayState
{
    Handshaking,
    ChallengeIssued,
    Executing,
    Succeeded,
    Failed,
    RolledBack,
    RecoveryRequired,
    Abandoned,
}

public enum ReplaySideEffectEvidence
{
    None,
    RecoveryRecordPresent,
    Unknown,
}

public interface IReplaySideEffectEvidenceProvider
{
    ReplaySideEffectEvidence Inspect(Guid requestId);
}

public sealed class UnknownReplaySideEffectEvidenceProvider : IReplaySideEffectEvidenceProvider
{
    public ReplaySideEffectEvidence Inspect(Guid requestId) => ReplaySideEffectEvidence.Unknown;
}

public sealed record ReplayRegistryRecord(
    int SchemaVersion,
    string ReplayKeySha256,
    Guid RequestId,
    BrokerCommand Command,
    ReplayState State,
    uint OwnerProcessId,
    DateTimeOffset OwnerProcessStartUtc,
    Guid OwnerNonce,
    Guid? ConnectionId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastUpdatedUtc,
    DateTimeOffset LeaseExpiresUtc,
    DateTimeOffset? RetentionExpiresUtc,
    string? TerminalCode);

public sealed class ReplayLease
{
    internal ReplayLease(string path, ReplayRegistryRecord record)
    {
        Path = path;
        OwnerProcessId = record.OwnerProcessId;
        OwnerProcessStartUtc = record.OwnerProcessStartUtc;
        OwnerNonce = record.OwnerNonce;
        ConnectionId = record.ConnectionId;
        RequestId = record.RequestId;
    }

    internal string Path { get; }

    internal uint OwnerProcessId { get; }

    internal DateTimeOffset OwnerProcessStartUtc { get; }

    internal Guid OwnerNonce { get; }

    public Guid RequestId { get; }

    public Guid? ConnectionId { get; internal set; }
}

public sealed record ReplayAcquireResult(
    ReplayLease? Lease,
    BrokerError? Error)
{
    public bool IsSuccess => Lease is not null;
}

public interface IReplayRegistry
{
    ValueTask<ReplayAcquireResult> AcquireAsync(
        BrokerAuthenticatedClient client,
        Guid requestId,
        BrokerCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<BrokerError?> MarkChallengeIssuedAsync(
        ReplayLease lease,
        Guid connectionId,
        CancellationToken cancellationToken = default);

    ValueTask<BrokerError?> MarkExecutingAsync(
        ReplayLease lease,
        CancellationToken cancellationToken = default);

    ValueTask<BrokerError?> RenewAsync(
        ReplayLease lease,
        CancellationToken cancellationToken = default);

    ValueTask<BrokerError?> CompleteAsync(
        ReplayLease lease,
        ReplayState terminalState,
        string? terminalCode,
        CancellationToken cancellationToken = default);
}

public sealed class FileReplayRegistry : IReplayRegistry
{
    public const string ProductionRoot = @"%ProgramData%\FolderSessionLock\Replay\v1";
    public const string ProductionMutexName = @"Global\FolderSessionLock.ReplayRegistry.v1";
    public const string FileExtension = ".fsrr";
    public const string TemporaryFileMarker = ".tmp-";
    public const int SchemaVersion = 1;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RenewalPeriod = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaximumExecutionDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> RecordFields =
    [
        "schemaVersion",
        "replayKeySha256",
        "requestId",
        "command",
        "state",
        "ownerProcessId",
        "ownerProcessStartUtc",
        "ownerNonce",
        "connectionId",
        "createdUtc",
        "lastUpdatedUtc",
        "leaseExpiresUtc",
        "retentionExpiresUtc",
        "terminalCode",
    ];

    private readonly string _root;
    private readonly IClock _clock;
    private readonly IReplaySideEffectEvidenceProvider _evidenceProvider;
    private readonly Func<Mutex> _mutexFactory;
    private readonly bool _createDirectory;

    internal FileReplayRegistry(
        string root,
        string mutexName,
        IClock clock,
        IReplaySideEffectEvidenceProvider evidenceProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        _root = Path.GetFullPath(root);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _evidenceProvider = evidenceProvider ?? throw new ArgumentNullException(nameof(evidenceProvider));
        _mutexFactory = () => new Mutex(false, mutexName);
        _createDirectory = true;
    }

    private FileReplayRegistry(
        string root,
        IClock clock,
        IReplaySideEffectEvidenceProvider evidenceProvider,
        Func<Mutex> mutexFactory)
    {
        _root = root;
        _clock = clock;
        _evidenceProvider = evidenceProvider;
        _mutexFactory = mutexFactory;
        _createDirectory = false;
    }

    public static FileReplayRegistry CreateProduction(
        ProtectedPathSet pathSet,
        IClock clock,
        IReplaySideEffectEvidenceProvider evidenceProvider)
    {
        return new FileReplayRegistry(
            (pathSet ?? throw new ArgumentNullException(nameof(pathSet))).ReplayDirectory,
            clock,
            evidenceProvider,
            CreateProtectedProductionMutex);
    }

    public async ValueTask<ReplayAcquireResult> AcquireAsync(
        BrokerAuthenticatedClient client,
        Guid requestId,
        BrokerCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_createDirectory)
        {
            Directory.CreateDirectory(_root);
        }
        else if (!Directory.Exists(_root))
        {
            return new ReplayAcquireResult(null, RecoveryRequiredError());
        }
        string key = CreateReplayKey(client.BrokerIdentity, requestId);
        string path = Path.Combine(_root, key + FileExtension);
        using Mutex mutex = _mutexFactory();
        await WaitAsync(mutex, cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            if (File.Exists(path))
            {
                ReplayRegistryRecord existing;
                try
                {
                    existing = ReadRecord(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    return new ReplayAcquireResult(null, RecoveryRequiredError());
                }

                BrokerError? existingError = HandleExisting(path, existing, now);
                if (existingError is not null)
                {
                    return new ReplayAcquireResult(null, existingError);
                }
            }

            using Process process = Process.GetCurrentProcess();
            var record = new ReplayRegistryRecord(
                SchemaVersion,
                key,
                requestId,
                command,
                ReplayState.Handshaking,
                checked((uint)process.Id),
                process.StartTime.ToUniversalTime(),
                Guid.NewGuid(),
                null,
                now,
                now,
                now.Add(LeaseDuration),
                null,
                null);
            try
            {
                WriteNew(path, record);
            }
            catch (IOException)
            {
                ReplayRegistryRecord concurrent = ReadRecord(path);
                return new ReplayAcquireResult(null, ErrorForExisting(concurrent, now));
            }

            return new ReplayAcquireResult(new ReplayLease(path, record), null);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public ValueTask<BrokerError?> MarkChallengeIssuedAsync(
        ReplayLease lease,
        Guid connectionId,
        CancellationToken cancellationToken = default) => UpdateAsync(
            lease,
            record => record with
            {
                State = ReplayState.ChallengeIssued,
                ConnectionId = connectionId,
                LastUpdatedUtc = _clock.UtcNow.ToUniversalTime(),
                LeaseExpiresUtc = _clock.UtcNow.ToUniversalTime().Add(LeaseDuration),
            },
            cancellationToken,
            connectionId);

    public ValueTask<BrokerError?> MarkExecutingAsync(
        ReplayLease lease,
        CancellationToken cancellationToken = default) => UpdateAsync(
            lease,
            record => record with
            {
                State = ReplayState.Executing,
                LastUpdatedUtc = _clock.UtcNow.ToUniversalTime(),
                LeaseExpiresUtc = _clock.UtcNow.ToUniversalTime().Add(LeaseDuration),
            },
            cancellationToken);

    public ValueTask<BrokerError?> RenewAsync(
        ReplayLease lease,
        CancellationToken cancellationToken = default) => UpdateAsync(
            lease,
            record => record with
            {
                LastUpdatedUtc = _clock.UtcNow.ToUniversalTime(),
                LeaseExpiresUtc = _clock.UtcNow.ToUniversalTime().Add(LeaseDuration),
            },
            cancellationToken);

    public ValueTask<BrokerError?> CompleteAsync(
        ReplayLease lease,
        ReplayState terminalState,
        string? terminalCode,
        CancellationToken cancellationToken = default)
    {
        if (terminalState is not (
            ReplayState.Succeeded
            or ReplayState.Failed
            or ReplayState.RolledBack
            or ReplayState.RecoveryRequired
            or ReplayState.Abandoned))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        }

        return UpdateAsync(
            lease,
            record => record with
            {
                State = terminalState,
                LastUpdatedUtc = _clock.UtcNow.ToUniversalTime(),
                LeaseExpiresUtc = _clock.UtcNow.ToUniversalTime(),
                RetentionExpiresUtc = terminalState == ReplayState.RecoveryRequired
                    ? null
                    : _clock.UtcNow.ToUniversalTime().Add(TerminalRetention),
                TerminalCode = terminalCode,
            },
            cancellationToken);
    }

    public static string CreateReplayKey(SessionIdentity brokerIdentity, Guid requestId)
    {
        string canonical = string.Join(
            '\n',
            "FSL-REPLAY-V1",
            brokerIdentity.AccountSid,
            brokerIdentity.LogonSid,
            brokerIdentity.WindowsSessionId.ToString(CultureInfo.InvariantCulture),
            requestId.ToString("D"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private async ValueTask<BrokerError?> UpdateAsync(
        ReplayLease lease,
        Func<ReplayRegistryRecord, ReplayRegistryRecord> update,
        CancellationToken cancellationToken,
        Guid? assignedConnectionId = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        using Mutex mutex = _mutexFactory();
        await WaitAsync(mutex, cancellationToken).ConfigureAwait(false);
        try
        {
            ReplayRegistryRecord current = ReadRecord(lease.Path);
            if (!Owns(current, lease))
            {
                return RecoveryRequiredError();
            }

            ReplayRegistryRecord next = update(current);
            WriteReplacement(lease.Path, next);
            if (assignedConnectionId is not null)
            {
                lease.ConnectionId = assignedConnectionId;
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return RecoveryRequiredError();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private BrokerError? HandleExisting(
        string path,
        ReplayRegistryRecord existing,
        DateTimeOffset now)
    {
        if (existing.State == ReplayState.RecoveryRequired)
        {
            return ReplayDetectedError();
        }

        if (IsActive(existing.State))
        {
            if (existing.LeaseExpiresUtc > now || IsOwnerAlive(existing))
            {
                return InProgressError();
            }

            ReplaySideEffectEvidence evidence = _evidenceProvider.Inspect(existing.RequestId);
            ReplayState state = evidence == ReplaySideEffectEvidence.None
                ? ReplayState.Abandoned
                : ReplayState.RecoveryRequired;
            string terminalCode = state == ReplayState.Abandoned
                ? BrokerErrorCodes.FSL_E_HANDSHAKE_EXPIRED
                : BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED;
            WriteReplacement(path, existing with
            {
                State = state,
                LastUpdatedUtc = now,
                LeaseExpiresUtc = now,
                RetentionExpiresUtc = state == ReplayState.RecoveryRequired
                    ? null
                    : now.Add(TerminalRetention),
                TerminalCode = terminalCode,
            });
            return ReplayDetectedError();
        }

        if (existing.RetentionExpiresUtc is null || existing.RetentionExpiresUtc > now)
        {
            return ReplayDetectedError();
        }

        File.Delete(path);
        return null;
    }

    private static BrokerError ErrorForExisting(ReplayRegistryRecord record, DateTimeOffset now) =>
        IsActive(record.State) && record.LeaseExpiresUtc > now
            ? InProgressError()
            : ReplayDetectedError();

    private static bool Owns(ReplayRegistryRecord record, ReplayLease lease) =>
        record.OwnerProcessId == lease.OwnerProcessId
        && record.OwnerProcessStartUtc == lease.OwnerProcessStartUtc
        && record.OwnerNonce == lease.OwnerNonce
        && record.ConnectionId == lease.ConnectionId;

    private static bool IsActive(ReplayState state) => state is
        ReplayState.Handshaking or ReplayState.ChallengeIssued or ReplayState.Executing;

    private static bool IsOwnerAlive(ReplayRegistryRecord record)
    {
        try
        {
            using Process process = Process.GetProcessById(checked((int)record.OwnerProcessId));
            return !process.HasExited && process.StartTime.ToUniversalTime() == record.OwnerProcessStartUtc;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        {
            return false;
        }
    }

    private static void WriteNew(string path, ReplayRegistryRecord record)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        WriteRecord(stream, record);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteReplacement(string path, ReplayRegistryRecord record)
    {
        string temporary = path[..^FileExtension.Length] + TemporaryFileMarker + Guid.NewGuid().ToString("D");
        try
        {
            WriteNew(temporary, record);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteRecord(Stream stream, ReplayRegistryRecord record)
    {
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", record.SchemaVersion);
        writer.WriteString("replayKeySha256", record.ReplayKeySha256);
        writer.WriteString("requestId", record.RequestId.ToString("D"));
        writer.WriteString("command", record.Command.ToString());
        writer.WriteString("state", record.State.ToString());
        writer.WriteNumber("ownerProcessId", record.OwnerProcessId);
        writer.WriteString("ownerProcessStartUtc", FormatTimestamp(record.OwnerProcessStartUtc));
        writer.WriteString("ownerNonce", record.OwnerNonce.ToString("D"));
        WriteNullableGuid(writer, "connectionId", record.ConnectionId);
        writer.WriteString("createdUtc", FormatTimestamp(record.CreatedUtc));
        writer.WriteString("lastUpdatedUtc", FormatTimestamp(record.LastUpdatedUtc));
        writer.WriteString("leaseExpiresUtc", FormatTimestamp(record.LeaseExpiresUtc));
        WriteNullableTimestamp(writer, "retentionExpiresUtc", record.RetentionExpiresUtc);
        WriteNullableString(writer, "terminalCode", record.TerminalCode);
        writer.WriteEndObject();
    }

    private static ReplayRegistryRecord ReadRecord(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using JsonDocument document = StrictDocument(bytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Select(property => property.Name).Any(name => !RecordFields.Contains(name))
            || RecordFields.Any(name => !root.TryGetProperty(name, out _))
            || root.EnumerateObject().Count() != RecordFields.Count)
        {
            throw new JsonException();
        }

        var record = new ReplayRegistryRecord(
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("replayKeySha256").GetString()!,
            ParseGuid(root.GetProperty("requestId")),
            ParseEnum<BrokerCommand>(root.GetProperty("command")),
            ParseEnum<ReplayState>(root.GetProperty("state")),
            root.GetProperty("ownerProcessId").GetUInt32(),
            ParseTimestamp(root.GetProperty("ownerProcessStartUtc")),
            ParseGuid(root.GetProperty("ownerNonce")),
            ParseNullableGuid(root.GetProperty("connectionId")),
            ParseTimestamp(root.GetProperty("createdUtc")),
            ParseTimestamp(root.GetProperty("lastUpdatedUtc")),
            ParseTimestamp(root.GetProperty("leaseExpiresUtc")),
            ParseNullableTimestamp(root.GetProperty("retentionExpiresUtc")),
            root.GetProperty("terminalCode").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("terminalCode").GetString());
        if (record.SchemaVersion != SchemaVersion
            || record.ReplayKeySha256.Length != 64
            || record.ReplayKeySha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || record.OwnerProcessId == 0
            || record.OwnerNonce == Guid.Empty)
        {
            throw new JsonException();
        }

        return record;
    }

    private static JsonDocument StrictDocument(ReadOnlyMemory<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes.Span);
        var properties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                properties.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.PropertyName
                && (properties.Count == 0 || !properties.Peek().Add(reader.GetString()!)))
            {
                throw new JsonException();
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                properties.Pop();
            }
        }

        return JsonDocument.Parse(bytes);
    }

    private static T ParseEnum<T>(JsonElement element) where T : struct, Enum =>
        element.ValueKind == JsonValueKind.String
        && Enum.TryParse(element.GetString(), ignoreCase: false, out T value)
        && Enum.IsDefined(value)
            ? value
            : throw new JsonException();

    private static Guid ParseGuid(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
        && Guid.TryParseExact(element.GetString(), "D", out Guid value)
        && value != Guid.Empty
        && element.GetString() == value.ToString("D")
            ? value
            : throw new JsonException();

    private static Guid? ParseNullableGuid(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : ParseGuid(element);

    private static DateTimeOffset ParseTimestamp(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParseExact(
            element.GetString(),
            BrokerProtocolConstants.UtcTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset value)
        && value.Offset == TimeSpan.Zero
            ? value
            : throw new JsonException();

    private static DateTimeOffset? ParseNullableTimestamp(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : ParseTimestamp(element);

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString(
        BrokerProtocolConstants.UtcTimestampFormat,
        CultureInfo.InvariantCulture);

    private static void WriteNullableGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value.Value.ToString("D"));
        }
    }

    private static void WriteNullableTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, FormatTimestamp(value.Value));
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static async ValueTask WaitAsync(Mutex mutex, CancellationToken cancellationToken)
    {
        try
        {
            while (!mutex.WaitOne(TimeSpan.FromMilliseconds(50)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
        catch (AbandonedMutexException)
        {
        }
    }

    private static BrokerError InProgressError() => new(
        BrokerErrorCodes.FSL_E_REQUEST_IN_PROGRESS,
        "The request is already being processed.",
        true,
        "requestId");

    private static BrokerError ReplayDetectedError() => new(
        BrokerErrorCodes.FSL_E_REPLAY_DETECTED,
        "The request has already been used.",
        false,
        "requestId");

    private static BrokerError RecoveryRequiredError() => new(
        BrokerErrorCodes.FSL_E_RECOVERY_REQUIRED,
        "The request requires recovery before it can continue.",
        false,
        null);

    private static Mutex CreateProtectedProductionMutex()
    {
        SecurityIdentifier[] subjects = ProductionSubjects().ToArray();
        string sddl = $"D:P(A;;GA;;;{subjects[0].Value})(A;;GA;;;{subjects[1].Value})(A;;GA;;;{subjects[2].Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                1,
                out nint descriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };
            SafeWaitHandle handle = CreateMutexEx(
                ref attributes,
                ProductionMutexName,
                0,
                0x001F0001);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var mutex = new Mutex();
            mutex.SafeWaitHandle = handle;
            return mutex;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static IEnumerable<SecurityIdentifier> ProductionSubjects()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        yield return WindowsServiceSid.RecoveryService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal nint SecurityDescriptor;
        internal int InheritHandle;
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexEx(
        ref SecurityAttributes mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
