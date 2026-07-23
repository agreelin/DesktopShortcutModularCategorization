using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Logging;

internal sealed class WindowsProtectedLogFilePlatform : IProtectedLogFilePlatform
{
    private readonly string _logsRoot;
    private readonly IRecoveryStoreFilePlatform _files;
    private readonly IProtectedLogFileSecurity _security;

    internal WindowsProtectedLogFilePlatform()
        : this(
            ProtectedPathSet.CreateProduction().LogsRoot,
            new WindowsRecoveryStoreFilePlatform(),
            new ProtectedLogFileSecurity())
    {
    }

    internal WindowsProtectedLogFilePlatform(
        string logsRoot,
        IRecoveryStoreFilePlatform files,
        IProtectedLogFileSecurity security)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        _logsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(logsRoot));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _security = security ?? throw new ArgumentNullException(nameof(security));
    }

    public Result<IProtectedLogFile> CreateNew(
        ProtectedLoggerMode mode,
        string leafName)
    {
        Result<SafeFileHandle> rootOpen = _files.OpenDirectory(_logsRoot);
        if (rootOpen.IsFailure)
        {
            return Failure<IProtectedLogFile>();
        }

        using SafeFileHandle root = rootOpen.Value;
        if (VerifyDirectory(root, _logsRoot).IsFailure)
        {
            return Failure<IProtectedLogFile>();
        }

        string modeDirectory = Path.Combine(_logsRoot, ModeDirectoryName(mode));
        Result<SafeFileHandle> directoryOpen = _files.OpenDirectory(modeDirectory);
        if (directoryOpen.IsFailure)
        {
            return Failure<IProtectedLogFile>();
        }

        using SafeFileHandle directory = directoryOpen.Value;
        if (VerifyDirectory(directory, modeDirectory).IsFailure)
        {
            return Failure<IProtectedLogFile>();
        }

        Result<SafeFileHandle> create = _files.CreateTemporary(directory, leafName);
        if (create.IsFailure)
        {
            return Failure<IProtectedLogFile>();
        }

        SafeFileHandle file = create.Value;
        Result secured = _security.ApplyAndVerifyFile(file);
        if (secured.IsFailure)
        {
            _ = _files.Delete(file);
            _ = _files.CloseAfterDisposition(file);
            return Failure<IProtectedLogFile>();
        }

        return Result<IProtectedLogFile>.Success(new WindowsProtectedLogFile(leafName, file));
    }

    public Result Write(
        IProtectedLogFile file,
        ReadOnlyMemory<byte> bytes,
        long offset)
    {
        if (file is not WindowsProtectedLogFile windowsFile)
        {
            return Failure();
        }

        try
        {
            RandomAccess.Write(windowsFile.Handle, bytes.Span, offset);
            return Result.Success();
        }
        catch (IOException)
        {
            return Failure();
        }
    }

    public Result Flush(IProtectedLogFile file) =>
        file is WindowsProtectedLogFile windowsFile
            && FlushFileBuffers(windowsFile.Handle)
                ? Result.Success()
                : Failure();

    internal static string ModeDirectoryName(ProtectedLoggerMode mode) => mode switch
    {
        ProtectedLoggerMode.ConsentBroker => "consent-broker",
        ProtectedLoggerMode.RecoveryService => "recovery-service",
        ProtectedLoggerMode.RecoveryOnce => "recovery-once",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private Result VerifyDirectory(SafeFileHandle handle, string expectedPath)
    {
        Result<NativeMethods.FileAttributeTagInfo> attributes = _files.GetAttributes(handle);
        Result<string> finalPath = _files.GetFinalPath(handle);
        if (attributes.IsFailure
            || finalPath.IsFailure
            || (attributes.Value.FileAttributes & NativeMethods.FileAttributeDirectory) == 0
            || (attributes.Value.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalPath.Value)),
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure();
        }

        return _security.VerifyDirectory(handle);
    }

    private static Result Failure() => Result.Failure(LoggerError());

    private static Result<T> Failure<T>() => Result<T>.Failure(LoggerError());

    private static Error LoggerError() => new(
        BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
        "The protected diagnostic logger could not be initialized.",
        ErrorCategory.UnrecoverableError);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle fileHandle);

    private sealed class WindowsProtectedLogFile(
        string leafName,
        SafeFileHandle handle) : IProtectedLogFile
    {
        internal SafeFileHandle Handle { get; } = handle;

        public string LeafName { get; } = leafName;

        public void Dispose() => Handle.Dispose();
    }
}

internal interface IProtectedLogFileSecurity
{
    Result VerifyDirectory(SafeFileHandle handle);

    Result ApplyAndVerifyFile(SafeFileHandle handle);

    Result VerifyFile(SafeFileHandle handle);
}

internal sealed class ProtectedLogFileSecurity : IProtectedLogFileSecurity
{
    private readonly WindowsRecoveryRecordFileSecurityPlatform _platform;
    private readonly IWindowsPrivilegeController _privileges;

    internal ProtectedLogFileSecurity()
        : this(new WindowsRecoveryRecordFileSecurityPlatform(), new WindowsPrivilegeController())
    {
    }

    internal ProtectedLogFileSecurity(
        WindowsRecoveryRecordFileSecurityPlatform platform,
        IWindowsPrivilegeController privileges)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));
    }

    public Result VerifyDirectory(SafeFileHandle handle) => Verify(handle, requireSingleLink: false);

    public Result ApplyAndVerifyFile(SafeFileHandle handle)
    {
        Result<RecoveryRecordFileSecurityEvidence> initial = _platform.Read(handle);
        Result<SecurityIdentifier> serviceSid = _platform.ResolveServiceSid();
        if (initial.IsFailure || serviceSid.IsFailure)
        {
            return Failure();
        }

        IWindowsPrivilegeLease? privilege = null;
        Result? operation = null;
        try
        {
            if (!string.Equals(
                initial.Value.OwnerSid,
                ProtectedPathAclPolicy.SystemSid,
                StringComparison.Ordinal))
            {
                Result<IWindowsPrivilegeLease> enable = _privileges.EnableRestorePrivilege();
                if (enable.IsFailure)
                {
                    return Failure();
                }

                privilege = enable.Value;
            }

            operation = _platform.SetOwner(
                handle,
                new SecurityIdentifier(ProtectedPathAclPolicy.SystemSid));
            if (operation.IsSuccess)
            {
                operation = _platform.SetDacl(handle, serviceSid.Value);
            }
        }
        finally
        {
            if (privilege is not null)
            {
                Result revert = privilege.Revert();
                privilege.Dispose();
                if (revert.IsFailure)
                {
                    operation = revert;
                }
            }
        }

        return operation is { IsFailure: true }
            ? Failure()
            : VerifyFile(handle);
    }

    public Result VerifyFile(SafeFileHandle handle) => Verify(handle, requireSingleLink: true);

    private Result Verify(SafeFileHandle handle, bool requireSingleLink)
    {
        Result<RecoveryRecordFileSecurityEvidence> read = _platform.Read(handle);
        Result<SecurityIdentifier> serviceSid = _platform.ResolveServiceSid();
        if (read.IsFailure || serviceSid.IsFailure)
        {
            return Failure();
        }

        RecoveryRecordFileSecurityEvidence evidence = read.Value;
        string[] expectedSids =
        [
            ProtectedPathAclPolicy.SystemSid,
            ProtectedPathAclPolicy.AdministratorsSid,
            serviceSid.Value.Value,
        ];
        if (!string.Equals(
                evidence.OwnerSid,
                ProtectedPathAclPolicy.SystemSid,
                StringComparison.Ordinal)
            || !evidence.DaclPresent
            || evidence.DaclIsNull
            || !evidence.DaclProtected
            || evidence.AclRevision != 2
            || evidence.Aces.Count != expectedSids.Length
            || (requireSingleLink && evidence.Identity.NumberOfLinks != 1))
        {
            return Failure();
        }

        for (int index = 0; index < expectedSids.Length; index++)
        {
            RecoveryRecordFileAce ace = evidence.Aces[index];
            if (!ace.IsQualified
                || ace.AceType != AceType.AccessAllowed
                || ace.AceQualifier != AceQualifier.AccessAllowed
                || ace.AceFlags != AceFlags.None
                || ace.AccessMask != 0x001F01FF
                || !string.Equals(ace.Sid, expectedSids[index], StringComparison.Ordinal)
                || ace.IsCallback
                || ace.IsObject)
            {
                return Failure();
            }
        }

        return Result.Success();
    }

    private static Result Failure() => Result.Failure(new Error(
        BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
        "The protected diagnostic logger could not be initialized.",
        ErrorCategory.UnrecoverableError));
}

internal sealed class WindowsProtectedLoggerFactory : IProtectedLoggerFactory
{
    private readonly Func<IProtectedLogFilePlatform> _filePlatformFactory;
    private readonly Func<IProtectedLogRetentionPlatform> _retentionPlatformFactory;
    private readonly Func<DateTimeOffset> _utcNow;

    internal WindowsProtectedLoggerFactory()
        : this(
            static () => new WindowsProtectedLogFilePlatform(),
            static () => new WindowsProtectedLogRetentionPlatform(),
            static () => DateTimeOffset.UtcNow)
    {
    }

    internal WindowsProtectedLoggerFactory(
        Func<IProtectedLogFilePlatform> filePlatformFactory,
        Func<IProtectedLogRetentionPlatform> retentionPlatformFactory,
        Func<DateTimeOffset> utcNow)
    {
        _filePlatformFactory = filePlatformFactory
            ?? throw new ArgumentNullException(nameof(filePlatformFactory));
        _retentionPlatformFactory = retentionPlatformFactory
            ?? throw new ArgumentNullException(nameof(retentionPlatformFactory));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public Result<ILoggerFactory> Create(ProtectedLoggerMode mode, Guid instanceId)
    {
        if (instanceId == Guid.Empty || !Enum.IsDefined(mode))
        {
            return Failure();
        }

        var retention = new ProtectedLogRetention(_retentionPlatformFactory());
        var provider = new ProtectedJsonLinesLoggerProvider(
            mode,
            instanceId,
            _filePlatformFactory(),
            _utcNow,
            beforeFileCreate: () => retention.Cleanup(_utcNow()));
        Result initialize = provider.Initialize();
        if (initialize.IsFailure)
        {
            provider.Dispose();
            return Failure();
        }

        return Result<ILoggerFactory>.Success(new ProtectedLoggerFactory(
            provider,
            retention,
            _utcNow));
    }

    private static Result<ILoggerFactory> Failure() => Result<ILoggerFactory>.Failure(new Error(
        BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
        "The protected diagnostic logger could not be initialized.",
        ErrorCategory.UnrecoverableError));
}
