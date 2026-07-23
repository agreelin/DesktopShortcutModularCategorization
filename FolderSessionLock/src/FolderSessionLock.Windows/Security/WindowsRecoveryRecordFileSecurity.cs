using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Security;

internal sealed class WindowsRecoveryRecordFileSecurity : IRecoveryRecordFileSecurity
{
    private readonly WindowsRecoveryRecordFileSecurityPlatform _platform;
    private readonly IWindowsPrivilegeController _privileges;

    internal WindowsRecoveryRecordFileSecurity()
        : this(
            new WindowsRecoveryRecordFileSecurityPlatform(),
            new WindowsPrivilegeController())
    {
    }

    internal WindowsRecoveryRecordFileSecurity(
        WindowsRecoveryRecordFileSecurityPlatform platform,
        IWindowsPrivilegeController privileges)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));
    }

    public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> ApplyAndVerifyAsync(
        SafeFileHandle fileHandle,
        RecoveryRecordFileKind fileKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileHandle);
        cancellationToken.ThrowIfCancellationRequested();
        if (fileKind != RecoveryRecordFileKind.TemporaryRecord)
        {
            return ValueTask.FromResult(Failure(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_MISMATCH));
        }

        Result<RecoveryRecordFileSecurityEvidence> initial = _platform.Read(fileHandle);
        if (initial.IsFailure)
        {
            return ValueTask.FromResult(Result<RecoveryRecordFileSecuritySnapshot>.Failure(
                initial.Error!));
        }

        Result<SecurityIdentifier> serviceSid = _platform.ResolveServiceSid();
        if (serviceSid.IsFailure)
        {
            return ValueTask.FromResult(Failure(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_SERVICE_SID_UNAVAILABLE));
        }

        IWindowsPrivilegeLease? privilege = null;
        Result? operationFailure = null;
        try
        {
            if (initial.Value.OwnerSid != ProtectedPathAclPolicy.SystemSid)
            {
                Result<IWindowsPrivilegeLease> enable = _privileges.EnableRestorePrivilege();
                if (enable.IsFailure)
                {
                    return ValueTask.FromResult(Failure(
                        BrokerErrorCodes.FSL_E_RECOVERY_FILE_OWNER_PRIVILEGE_UNAVAILABLE));
                }

                privilege = enable.Value;
            }

            Result owner = _platform.SetOwner(
                fileHandle,
                new SecurityIdentifier(ProtectedPathAclPolicy.SystemSid));
            if (owner.IsFailure)
            {
                operationFailure = owner;
            }
            else
            {
                Result dacl = _platform.SetDacl(fileHandle, serviceSid.Value);
                if (dacl.IsFailure)
                {
                    operationFailure = dacl;
                }
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
                    operationFailure = revert;
                }
            }
        }

        return operationFailure is not null
            ? ValueTask.FromResult(Result<RecoveryRecordFileSecuritySnapshot>.Failure(
                operationFailure.Error!))
            : VerifyAsync(fileHandle, fileKind, cancellationToken);
    }

    public ValueTask<Result<RecoveryRecordFileSecuritySnapshot>> VerifyAsync(
        SafeFileHandle fileHandle,
        RecoveryRecordFileKind fileKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileHandle);
        cancellationToken.ThrowIfCancellationRequested();
        Result<RecoveryRecordFileSecurityEvidence> read = _platform.Read(fileHandle);
        if (read.IsFailure)
        {
            return ValueTask.FromResult(Result<RecoveryRecordFileSecuritySnapshot>.Failure(
                read.Error!));
        }

        Result<SecurityIdentifier> serviceSid = _platform.ResolveServiceSid();
        if (serviceSid.IsFailure)
        {
            return ValueTask.FromResult(Failure(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_SERVICE_SID_UNAVAILABLE));
        }

        RecoveryRecordFileSecurityEvidence evidence = read.Value;
        string? error = Validate(evidence, serviceSid.Value.Value);
        return error is not null
            ? ValueTask.FromResult(Failure(error))
            : ValueTask.FromResult(Result<RecoveryRecordFileSecuritySnapshot>.Success(new(
                fileKind,
                evidence.Identity,
                evidence.OwnerSid,
                evidence.DaclPresent,
                evidence.DaclIsNull,
                evidence.DaclProtected,
                evidence.Aces.Count)));
    }

    private static string? Validate(
        RecoveryRecordFileSecurityEvidence evidence,
        string serviceSid)
    {
        if (evidence.OwnerSid != ProtectedPathAclPolicy.SystemSid)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_FILE_OWNER_MISMATCH;
        }

        if (!evidence.DaclPresent)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_MISSING;
        }

        if (evidence.DaclIsNull)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_NULL;
        }

        if (!evidence.DaclProtected
            || evidence.Aces.Any(ace => (ace.AceFlags & AceFlags.Inherited) != 0))
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_FILE_INHERITANCE_INVALID;
        }

        string[] expectedSids =
        [
            ProtectedPathAclPolicy.SystemSid,
            ProtectedPathAclPolicy.AdministratorsSid,
            serviceSid,
        ];
        if (evidence.AclRevision != 2 || evidence.Aces.Count != expectedSids.Length)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_MISMATCH;
        }

        for (int index = 0; index < expectedSids.Length; index++)
        {
            RecoveryRecordFileAce ace = evidence.Aces[index];
            if (!ace.IsQualified
                || ace.AceType != AceType.AccessAllowed
                || ace.AceQualifier != AceQualifier.AccessAllowed
                || ace.AceFlags != AceFlags.None
                || ace.AccessMask != 0x001F01FF
                || ace.Sid != expectedSids[index]
                || ace.IsCallback
                || ace.IsObject)
            {
                return BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_MISMATCH;
            }
        }

        return evidence.Identity.NumberOfLinks == 1
            ? null
            : BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH;
    }

    private static Result<RecoveryRecordFileSecuritySnapshot> Failure(string code) =>
        Result<RecoveryRecordFileSecuritySnapshot>.Failure(new Error(
            code,
            code,
            ErrorCategory.UnrecoverableError));
}
