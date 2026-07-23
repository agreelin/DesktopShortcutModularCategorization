using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Security;

public sealed class WindowsProtectedPathSecurityVerifier : IProtectedPathSecurityVerifier
{
    private readonly ProtectedPathSet _pathSet;
    private readonly WindowsProtectedPathSecurityPlatform _platform;
    private readonly ProtectedPathAclPolicy _aclPolicy;

    public WindowsProtectedPathSecurityVerifier(ProtectedPathSet pathSet)
        : this(pathSet, new WindowsProtectedPathSecurityPlatform(), new ProtectedPathAclPolicy())
    {
    }

    internal WindowsProtectedPathSecurityVerifier(
        ProtectedPathSet pathSet,
        WindowsProtectedPathSecurityPlatform platform,
        ProtectedPathAclPolicy aclPolicy)
    {
        _pathSet = pathSet ?? throw new ArgumentNullException(nameof(pathSet));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _aclPolicy = aclPolicy ?? throw new ArgumentNullException(nameof(aclPolicy));
    }

    public ValueTask<ProtectedPathSecurityCheckResult> VerifyAsync(
        ProtectedPathSecurityCheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string fixedPath = _pathSet.GetExpectedPath(request.PathKind);
        string normalizedPath;
        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixedPath));
            if (!string.Equals(
                    normalizedPath,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ExpectedPath)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_NOT_FOUND);
        }

        if (!_platform.DirectoryExists(normalizedPath))
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_NOT_FOUND);
        }

        Result<SafeFileHandle> open = _platform.OpenDirectory(normalizedPath);
        if (open.IsFailure)
        {
            return Result(open.Error!.Code);
        }

        using SafeFileHandle handle = open.Value;
        Result<NativeMethods.FileAttributeTagInfo> attributes = _platform.GetAttributes(handle);
        if (attributes.IsFailure)
        {
            return Result(attributes.Error!.Code);
        }

        if ((attributes.Value.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_REPARSE_POINT);
        }

        Result<string> finalPath = _platform.GetFinalPath(handle);
        if (finalPath.IsFailure
            || !string.Equals(normalizedPath, finalPath.Value, StringComparison.OrdinalIgnoreCase))
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_FINAL_PATH_MISMATCH);
        }

        string root = Path.GetPathRoot(normalizedPath)!;
        Result<string> fileSystem = _platform.GetFileSystemName(handle);
        if (_platform.GetDriveType(root) != NativeMethods.DriveFixed
            || fileSystem.IsFailure
            || !string.Equals(fileSystem.Value, "NTFS", StringComparison.Ordinal))
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_VOLUME_UNSUPPORTED);
        }

        Result<DirectoryIdentity> beforeIdentity = _platform.GetIdentity(handle);
        if (beforeIdentity.IsFailure)
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_UNAVAILABLE);
        }

        Result<ProtectedPathSecurityDescriptor> security = _platform.ReadSecurity(handle);
        if (security.IsFailure)
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_SECURITY_READ_FAILED);
        }

        if (!security.Value.DaclPresent)
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISSING);
        }

        if (security.Value.DaclNull)
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_NULL);
        }

        string? policyError = _aclPolicy.Validate(request.PathKind, security.Value);
        if (policyError is not null)
        {
            return Result(policyError);
        }

        Result<DirectoryIdentity> afterIdentity = _platform.GetIdentity(handle);
        if (afterIdentity.IsFailure)
        {
            return Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_UNAVAILABLE);
        }

        return beforeIdentity.Value == afterIdentity.Value
            ? ValueTask.FromResult(new ProtectedPathSecurityCheckResult(true, null))
            : Result(BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_CHANGED);
    }

    private static ValueTask<ProtectedPathSecurityCheckResult> Result(string errorCode) =>
        ValueTask.FromResult(new ProtectedPathSecurityCheckResult(false, errorCode));
}
