using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FolderSessionLock.Broker.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Logging;

internal sealed record ProtectedLogArtifact(
    ProtectedLoggerMode Mode,
    string LeafName,
    DateTimeOffset LastWriteUtc,
    long Length,
    bool IsActive,
    bool IsSafe,
    IProtectedLogRetentionFile? File) : IDisposable
{
    public void Dispose() => File?.Dispose();
}

internal interface IProtectedLogRetentionFile : IDisposable
{
}

internal interface IProtectedLogRetentionPlatform
{
    Result<IReadOnlyList<ProtectedLogArtifact>> Enumerate(ProtectedLoggerMode mode);

    Result Delete(ProtectedLogArtifact artifact);
}

internal sealed class ProtectedLogRetention
{
    internal static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(14);
    internal const int MaximumClosedFilesPerMode = 32;
    internal const long MaximumTotalBytes = 256L * 1024 * 1024;
    private readonly IProtectedLogRetentionPlatform _platform;
    private readonly int _maximumClosedFilesPerMode;
    private readonly long _maximumTotalBytes;

    internal ProtectedLogRetention(
        IProtectedLogRetentionPlatform platform,
        int maximumClosedFilesPerMode = MaximumClosedFilesPerMode,
        long maximumTotalBytes = MaximumTotalBytes)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        if (maximumClosedFilesPerMode < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumClosedFilesPerMode));
        }

        if (maximumTotalBytes < 0 || maximumTotalBytes > MaximumTotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));
        }

        _maximumClosedFilesPerMode = maximumClosedFilesPerMode;
        _maximumTotalBytes = maximumTotalBytes;
    }

    internal Result Cleanup(DateTimeOffset nowUtc)
    {
        var artifacts = new List<ProtectedLogArtifact>();
        try
        {
            foreach (ProtectedLoggerMode mode in Enum.GetValues<ProtectedLoggerMode>())
            {
                Result<IReadOnlyList<ProtectedLogArtifact>> enumerate = _platform.Enumerate(mode);
                if (enumerate.IsFailure)
                {
                    return enumerate.Error!.Code == BrokerErrorCodes.FSL_E_PROTECTED_LOG_ARTIFACT_INVALID
                        ? ArtifactInvalid()
                        : LoggerUnavailable();
                }

                artifacts.AddRange(enumerate.Value);
            }

            bool invalidArtifact = artifacts.Any(artifact => !artifact.IsSafe);
            DateTimeOffset cutoff = nowUtc.ToUniversalTime() - RetentionPeriod;
            foreach (ProtectedLogArtifact artifact in Ordered(artifacts)
                .Where(artifact => artifact.IsSafe
                    && !artifact.IsActive
                    && artifact.LastWriteUtc < cutoff)
                .ToArray())
            {
                Result deletion = Delete(artifact, artifacts);
                if (deletion.IsFailure)
                {
                    return deletion;
                }
            }

            foreach (ProtectedLoggerMode mode in Enum.GetValues<ProtectedLoggerMode>())
            {
                ProtectedLogArtifact[] closed = Ordered(artifacts)
                    .Where(artifact => artifact.Mode == mode
                        && artifact.IsSafe
                        && !artifact.IsActive)
                    .ToArray();
                foreach (ProtectedLogArtifact artifact in closed
                    .Take(Math.Max(0, closed.Length - _maximumClosedFilesPerMode)))
                {
                    Result deletion = Delete(artifact, artifacts);
                    if (deletion.IsFailure)
                    {
                        return deletion;
                    }
                }
            }

            long totalBytes = artifacts.Sum(artifact => artifact.Length);
            foreach (ProtectedLogArtifact artifact in Ordered(artifacts)
                .Where(artifact => artifact.IsSafe && !artifact.IsActive)
                .ToArray())
            {
                if (totalBytes <= _maximumTotalBytes)
                {
                    break;
                }

                long length = artifact.Length;
                Result deletion = Delete(artifact, artifacts);
                if (deletion.IsFailure)
                {
                    return deletion;
                }

                totalBytes -= length;
            }

            if (artifacts.Sum(artifact => artifact.Length) > _maximumTotalBytes)
            {
                return LoggerUnavailable();
            }

            return invalidArtifact ? ArtifactInvalid() : Result.Success();
        }
        finally
        {
            foreach (ProtectedLogArtifact artifact in artifacts)
            {
                artifact.Dispose();
            }
        }
    }

    private Result Delete(
        ProtectedLogArtifact artifact,
        ICollection<ProtectedLogArtifact> artifacts)
    {
        Result deletion = _platform.Delete(artifact);
        if (deletion.IsFailure)
        {
            return LoggerUnavailable();
        }

        artifacts.Remove(artifact);
        artifact.Dispose();
        return Result.Success();
    }

    private static IOrderedEnumerable<ProtectedLogArtifact> Ordered(
        IEnumerable<ProtectedLogArtifact> artifacts) => artifacts
            .OrderBy(artifact => artifact.LastWriteUtc)
            .ThenBy(artifact => artifact.LeafName, StringComparer.Ordinal);

    private static Result ArtifactInvalid() => Result.Failure(new Error(
        BrokerErrorCodes.FSL_E_PROTECTED_LOG_ARTIFACT_INVALID,
        BrokerErrorCodes.FSL_E_PROTECTED_LOG_ARTIFACT_INVALID,
        ErrorCategory.UnrecoverableError));

    private static Result LoggerUnavailable() => Result.Failure(new Error(
        BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
        "The protected diagnostic logger could not be initialized.",
        ErrorCategory.UnrecoverableError));
}

internal static partial class ProtectedLogFileName
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";

    internal static bool TryParse(
        string leafName,
        out DateTimeOffset startedUtc,
        out uint processId,
        out Guid instanceId,
        out int rotationIndex)
    {
        startedUtc = default;
        processId = default;
        instanceId = default;
        rotationIndex = default;
        Match match = FileNamePattern().Match(leafName);
        return match.Success
            && DateTimeOffset.TryParseExact(
                match.Groups["started"].Value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out startedUtc)
            && uint.TryParse(
                match.Groups["pid"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processId)
            && processId != 0
            && Guid.TryParseExact(match.Groups["instance"].Value, "D", out instanceId)
            && instanceId != Guid.Empty
            && string.Equals(
                instanceId.ToString("D"),
                match.Groups["instance"].Value,
                StringComparison.Ordinal)
            && int.TryParse(
                match.Groups["rotation"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out rotationIndex)
            && rotationIndex is >= 0 and <= ProtectedJsonLinesLoggerProvider.MaximumRotationIndex;
    }

    [GeneratedRegex(
        "\\A(?<started>[0-9]{8}T[0-9]{13}Z)-(?<pid>0|[1-9][0-9]{0,9})-(?<instance>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})-(?<rotation>[0-9]{4})\\.jsonl\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}

internal sealed class WindowsProtectedLogRetentionPlatform : IProtectedLogRetentionPlatform
{
    private const uint DeleteAccess = 0x00010000;
    private const int FileBasicInfo = 0;
    private const int ErrorSharingViolation = 32;
    private readonly string _logsRoot;
    private readonly IRecoveryStoreFilePlatform _files;
    private readonly IProtectedLogFileSecurity _security;

    internal WindowsProtectedLogRetentionPlatform()
        : this(
            ProtectedPathSet.CreateProduction().LogsRoot,
            new WindowsRecoveryStoreFilePlatform(),
            new ProtectedLogFileSecurity())
    {
    }

    internal WindowsProtectedLogRetentionPlatform(
        string logsRoot,
        IRecoveryStoreFilePlatform files,
        IProtectedLogFileSecurity security)
    {
        _logsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(logsRoot));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _security = security ?? throw new ArgumentNullException(nameof(security));
    }

    public Result<IReadOnlyList<ProtectedLogArtifact>> Enumerate(ProtectedLoggerMode mode)
    {
        string modeDirectory = Path.Combine(
            _logsRoot,
            WindowsProtectedLogFilePlatform.ModeDirectoryName(mode));
        try
        {
            string[] expectedDirectories = Enum.GetValues<ProtectedLoggerMode>()
                .Select(WindowsProtectedLogFilePlatform.ModeDirectoryName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] actualDirectories = Directory.EnumerateDirectories(_logsRoot)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray()!;
            if (!actualDirectories.SequenceEqual(expectedDirectories, StringComparer.Ordinal))
            {
                return ArtifactInvalid<IReadOnlyList<ProtectedLogArtifact>>();
            }

            var artifacts = new List<ProtectedLogArtifact>();
            foreach (string path in Directory.EnumerateFiles(modeDirectory))
            {
                string leafName = Path.GetFileName(path);
                if (!ProtectedLogFileName.TryParse(
                    leafName,
                    out _,
                    out uint processId,
                    out _,
                    out _))
                {
                    artifacts.Add(new(mode, leafName, DateTimeOffset.MinValue, 0, false, false, null));
                    continue;
                }

                SafeFileHandle handle = NativeMethods.CreateFile(
                    path,
                    NativeMethods.FileReadData
                        | NativeMethods.FileReadAttributes
                        | NativeMethods.ReadControl
                        | DeleteAccess,
                    NativeMethods.FileShareRead
                        | NativeMethods.FileShareWrite
                        | NativeMethods.FileShareDelete,
                    nint.Zero,
                    NativeMethods.OpenExisting,
                    NativeMethods.FileFlagOpenReparsePoint,
                    nint.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    if (error == ErrorSharingViolation && IsProcessAlive(processId))
                    {
                        artifacts.Add(new(
                            mode,
                            leafName,
                            DateTimeOffset.MaxValue,
                            0,
                            true,
                            true,
                            null));
                        continue;
                    }

                    return ArtifactInvalid<IReadOnlyList<ProtectedLogArtifact>>();
                }

                Result<ProtectedLogArtifact> artifact = ReadArtifact(
                    mode,
                    modeDirectory,
                    path,
                    leafName,
                    handle);
                if (artifact.IsFailure)
                {
                    handle.Dispose();
                    artifacts.Add(new(mode, leafName, DateTimeOffset.MinValue, 0, false, false, null));
                }
                else
                {
                    artifacts.Add(artifact.Value);
                }
            }

            return Result<IReadOnlyList<ProtectedLogArtifact>>.Success(artifacts);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            return LoggerUnavailable<IReadOnlyList<ProtectedLogArtifact>>();
        }
    }

    public Result Delete(ProtectedLogArtifact artifact)
    {
        if (!artifact.IsSafe
            || artifact.IsActive
            || artifact.File is not WindowsProtectedLogRetentionFile file)
        {
            return ArtifactInvalid();
        }

        Result delete = _files.Delete(file.Handle);
        if (delete.IsFailure)
        {
            return LoggerUnavailable();
        }

        Result close = _files.CloseAfterDisposition(file.Handle);
        file.MarkClosed();
        if (close.IsFailure)
        {
            return LoggerUnavailable();
        }

        string path = Path.Combine(
            _logsRoot,
            WindowsProtectedLogFilePlatform.ModeDirectoryName(artifact.Mode),
            artifact.LeafName);
        return File.Exists(path) ? LoggerUnavailable() : Result.Success();
    }

    private Result<ProtectedLogArtifact> ReadArtifact(
        ProtectedLoggerMode mode,
        string modeDirectory,
        string path,
        string leafName,
        SafeFileHandle handle)
    {
        Result<NativeMethods.FileAttributeTagInfo> attributes = _files.GetAttributes(handle);
        Result<string> finalPath = _files.GetFinalPath(handle);
        Result security = _security.VerifyFile(handle);
        if (attributes.IsFailure
            || finalPath.IsFailure
            || security.IsFailure
            || (attributes.Value.FileAttributes & NativeMethods.FileAttributeDirectory) != 0
            || (attributes.Value.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0
            || !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(finalPath.Value),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(finalPath.Value)),
                Path.GetFullPath(modeDirectory),
                StringComparison.OrdinalIgnoreCase)
            || NativeMethods.GetFileStandardInfo(
                handle,
                NativeMethods.FileInfoByHandleClass.FileStandardInfo,
                out NativeMethods.FileStandardInfo standard,
                (uint)Marshal.SizeOf<NativeMethods.FileStandardInfo>()) == 0
            || standard.NumberOfLinks != 1
            || !GetFileInformationByHandleEx(
                handle,
                FileBasicInfo,
                out FileBasicInformation basic,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            return ArtifactInvalid<ProtectedLogArtifact>();
        }

        return Result<ProtectedLogArtifact>.Success(new(
            mode,
            leafName,
            new DateTimeOffset(DateTime.FromFileTimeUtc(basic.LastWriteTime), TimeSpan.Zero),
            standard.EndOfFile,
            false,
            true,
            new WindowsProtectedLogRetentionFile(handle)));
    }

    private static bool IsProcessAlive(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));
            return !process.HasExited;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or OverflowException)
        {
            return false;
        }
    }

    private static Result ArtifactInvalid() => Result.Failure(new Error(
        BrokerErrorCodes.FSL_E_PROTECTED_LOG_ARTIFACT_INVALID,
        BrokerErrorCodes.FSL_E_PROTECTED_LOG_ARTIFACT_INVALID,
        ErrorCategory.UnrecoverableError));

    private static Result<T> ArtifactInvalid<T>() => Result<T>.Failure(new Error(
        BrokerErrorCodes.FSL_E_PROTECTED_LOG_ARTIFACT_INVALID,
        BrokerErrorCodes.FSL_E_PROTECTED_LOG_ARTIFACT_INVALID,
        ErrorCategory.UnrecoverableError));

    private static Result LoggerUnavailable() => Result.Failure(new Error(
        BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
        "The protected diagnostic logger could not be initialized.",
        ErrorCategory.UnrecoverableError));

    private static Result<T> LoggerUnavailable<T>() => Result<T>.Failure(new Error(
        BrokerErrorCodes.FSL_E_PROTECTED_LOGGER_UNAVAILABLE,
        "The protected diagnostic logger could not be initialized.",
        ErrorCategory.UnrecoverableError));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileBasicInformation fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    private sealed class WindowsProtectedLogRetentionFile(
        SafeFileHandle handle) : IProtectedLogRetentionFile
    {
        private bool _closed;

        internal SafeFileHandle Handle { get; } = handle;

        internal void MarkClosed() => _closed = true;

        public void Dispose()
        {
            if (!_closed)
            {
                Handle.Dispose();
                _closed = true;
            }
        }
    }
}
