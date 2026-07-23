using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery;

internal enum RecoveryReadinessObjectKind
{
    Directory,
    CanonicalFile,
    TemporaryFile,
}

internal interface IRecoveryReadinessFileSecurity
{
    ValueTask<Result<RecoveryRecordFileIdentity>> ApplyAndVerifyAsync(
        SafeFileHandle handle,
        RecoveryReadinessObjectKind kind,
        CancellationToken cancellationToken);

    ValueTask<Result<RecoveryRecordFileIdentity>> VerifyAsync(
        SafeFileHandle handle,
        RecoveryReadinessObjectKind kind,
        CancellationToken cancellationToken);
}

internal sealed class RecoveryReadinessFileSecurity : IRecoveryReadinessFileSecurity
{
    private readonly WindowsRecoveryRecordFileSecurityPlatform _platform;
    private readonly IWindowsPrivilegeController _privileges;

    internal RecoveryReadinessFileSecurity()
        : this(new RecoveryReadinessSecurityPlatform(), new WindowsPrivilegeController())
    {
    }

    internal RecoveryReadinessFileSecurity(
        WindowsRecoveryRecordFileSecurityPlatform platform,
        IWindowsPrivilegeController privileges)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));
    }

    public ValueTask<Result<RecoveryRecordFileIdentity>> ApplyAndVerifyAsync(
        SafeFileHandle handle,
        RecoveryReadinessObjectKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();
        if (kind != RecoveryReadinessObjectKind.TemporaryFile)
        {
            return ValueTask.FromResult(Failure());
        }

        Result<RecoveryRecordFileSecurityEvidence> initial = _platform.Read(handle);
        if (initial.IsFailure)
        {
            return ValueTask.FromResult(Failure());
        }

        Result<SecurityIdentifier> serviceSid = _platform.ResolveServiceSid();
        if (serviceSid.IsFailure)
        {
            return ValueTask.FromResult(Failure());
        }

        IWindowsPrivilegeLease? lease = null;
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
                    return ValueTask.FromResult(Failure());
                }

                lease = enable.Value;
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
            if (lease is not null)
            {
                Result revert = lease.Revert();
                lease.Dispose();
                if (revert.IsFailure)
                {
                    operation = revert;
                }
            }
        }

        return operation is { IsFailure: true }
            ? ValueTask.FromResult(Failure())
            : VerifyAsync(handle, kind, cancellationToken);
    }

    public ValueTask<Result<RecoveryRecordFileIdentity>> VerifyAsync(
        SafeFileHandle handle,
        RecoveryReadinessObjectKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();
        Result<RecoveryRecordFileSecurityEvidence> read = _platform.Read(handle);
        Result<SecurityIdentifier> serviceSid = _platform.ResolveServiceSid();
        if (read.IsFailure || serviceSid.IsFailure)
        {
            return ValueTask.FromResult(Failure());
        }

        RecoveryRecordFileSecurityEvidence evidence = read.Value;
        int usersMask = kind == RecoveryReadinessObjectKind.Directory
            ? 0x001200A9
            : 0x00120089;
        string[] expectedSids =
        [
            ProtectedPathAclPolicy.SystemSid,
            ProtectedPathAclPolicy.AdministratorsSid,
            serviceSid.Value.Value,
            ProtectedPathAclPolicy.UsersSid,
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
            || (kind != RecoveryReadinessObjectKind.Directory
                && evidence.Identity.NumberOfLinks != 1))
        {
            return ValueTask.FromResult(Failure());
        }

        for (int index = 0; index < expectedSids.Length; index++)
        {
            RecoveryRecordFileAce ace = evidence.Aces[index];
            int expectedMask = index == 3 ? usersMask : 0x001F01FF;
            if (!ace.IsQualified
                || ace.AceType != AceType.AccessAllowed
                || ace.AceQualifier != AceQualifier.AccessAllowed
                || ace.AceFlags != AceFlags.None
                || ace.AccessMask != expectedMask
                || !string.Equals(ace.Sid, expectedSids[index], StringComparison.Ordinal)
                || ace.IsCallback
                || ace.IsObject)
            {
                return ValueTask.FromResult(Failure());
            }
        }

        return ValueTask.FromResult(Result<RecoveryRecordFileIdentity>.Success(evidence.Identity));
    }

    private static Result<RecoveryRecordFileIdentity> Failure() =>
        Result<RecoveryRecordFileIdentity>.Failure(new Error(
            BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
            BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
            ErrorCategory.UnrecoverableError));

    private sealed class RecoveryReadinessSecurityPlatform
        : WindowsRecoveryRecordFileSecurityPlatform
    {
        internal override Result SetDacl(
            SafeFileHandle fileHandle,
            SecurityIdentifier serviceSid)
        {
            var acl = new RawAcl(2, 4);
            acl.InsertAce(0, Allow(ProtectedPathAclPolicy.SystemSid, 0x001F01FF));
            acl.InsertAce(1, Allow(ProtectedPathAclPolicy.AdministratorsSid, 0x001F01FF));
            acl.InsertAce(2, Allow(serviceSid.Value, 0x001F01FF));
            acl.InsertAce(3, Allow(ProtectedPathAclPolicy.UsersSid, 0x00120089));
            byte[] bytes = new byte[acl.BinaryLength];
            acl.GetBinaryForm(bytes, 0);
            unsafe
            {
                fixed (byte* dacl = bytes)
                {
                    return NativeMethods.SetSecurityInfo(
                            fileHandle,
                            NativeMethods.SeObjectType.FileObject,
                            NativeMethods.DaclSecurityInformation
                                | NativeMethods.ProtectedDaclSecurityInformation,
                            nint.Zero,
                            nint.Zero,
                            (nint)dacl,
                            nint.Zero) == NativeMethods.ErrorSuccess
                        ? Result.Success()
                        : Result.Failure(new Error(
                            BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
                            BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
                            ErrorCategory.UnrecoverableError));
                }
            }
        }

        private static CommonAce Allow(string sid, int accessMask) => new(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            accessMask,
            new SecurityIdentifier(sid),
            isCallback: false,
            opaque: null);
    }
}
