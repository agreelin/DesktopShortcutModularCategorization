using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Broker.Logging;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.BrokerLogging.Tests;

public sealed class ProtectedLogSecurityTests
{
    [Fact]
    public void VerifyFile_AcceptsOnlySystemOwnerExactThreeAceProtectedDaclAndOneLink()
    {
        var platform = new FakeSecurityPlatform(ValidEvidence());
        var security = new ProtectedLogFileSecurity(platform, new FakePrivileges());
        using var handle = new SafeFileHandle(new nint(1), ownsHandle: false);

        Assert.True(security.VerifyFile(handle).IsSuccess);
        platform.Evidence = platform.Evidence with { OwnerSid = ProtectedPathAclPolicy.AdministratorsSid };
        Assert.True(security.VerifyFile(handle).IsFailure);
        platform.Evidence = ValidEvidence() with { DaclProtected = false };
        Assert.True(security.VerifyFile(handle).IsFailure);
        platform.Evidence = ValidEvidence() with
        {
            Aces = [.. ValidEvidence().Aces, ValidEvidence().Aces[0]],
        };
        Assert.True(security.VerifyFile(handle).IsFailure);
        platform.Evidence = ValidEvidence() with
        {
            Identity = ValidEvidence().Identity with { NumberOfLinks = 2 },
        };
        Assert.True(security.VerifyFile(handle).IsFailure);
    }

    [Fact]
    public void VerifyDirectory_AllowsDirectoryLinkCountButRequiresTheSameExactSecurity()
    {
        var platform = new FakeSecurityPlatform(ValidEvidence() with
        {
            Identity = ValidEvidence().Identity with { NumberOfLinks = 4 },
        });
        var security = new ProtectedLogFileSecurity(platform, new FakePrivileges());
        using var handle = new SafeFileHandle(new nint(1), ownsHandle: false);

        Assert.True(security.VerifyDirectory(handle).IsSuccess);
        platform.Evidence = platform.Evidence with
        {
            Aces = platform.Evidence.Aces.Select((ace, index) =>
                index == 2 ? ace with { AceFlags = AceFlags.Inherited } : ace).ToArray(),
        };
        Assert.True(security.VerifyDirectory(handle).IsFailure);
    }

    [Fact]
    public void ApplyAndVerifyFile_UsesRestorePrivilegeAndFailsWhenRevertFails()
    {
        RecoveryRecordFileSecurityEvidence evidence = ValidEvidence() with
        {
            OwnerSid = ProtectedPathAclPolicy.AdministratorsSid,
        };
        var platform = new FakeSecurityPlatform(evidence);
        var privileges = new FakePrivileges { FailRevert = true };
        var security = new ProtectedLogFileSecurity(platform, privileges);
        using var handle = new SafeFileHandle(new nint(1), ownsHandle: false);

        Result result = security.ApplyAndVerifyFile(handle);

        Assert.True(result.IsFailure);
        Assert.Equal(1, privileges.EnableCount);
        Assert.Equal(1, privileges.RevertCount);
        Assert.Equal(1, platform.SetOwnerCount);
        Assert.Equal(1, platform.SetDaclCount);
    }

    private static RecoveryRecordFileSecurityEvidence ValidEvidence()
    {
        string serviceSid = WindowsServiceSid.RecoveryService.Value;
        return new(
            new RecoveryRecordFileIdentity(1, 2, 3, 1),
            ProtectedPathAclPolicy.SystemSid,
            true,
            false,
            true,
            2,
            [
                Ace(ProtectedPathAclPolicy.SystemSid),
                Ace(ProtectedPathAclPolicy.AdministratorsSid),
                Ace(serviceSid),
            ]);
    }

    private static RecoveryRecordFileAce Ace(string sid) => new(
        AceType.AccessAllowed,
        AceFlags.None,
        0x001F01FF,
        sid,
        AceQualifier.AccessAllowed,
        false,
        false,
        true);

    private sealed class FakeSecurityPlatform(RecoveryRecordFileSecurityEvidence evidence)
        : WindowsRecoveryRecordFileSecurityPlatform
    {
        internal RecoveryRecordFileSecurityEvidence Evidence { get; set; } = evidence;
        internal int SetOwnerCount { get; private set; }
        internal int SetDaclCount { get; private set; }

        internal override Result<RecoveryRecordFileSecurityEvidence> Read(SafeFileHandle fileHandle) =>
            Result<RecoveryRecordFileSecurityEvidence>.Success(Evidence);

        internal override Result<SecurityIdentifier> ResolveServiceSid() =>
            Result<SecurityIdentifier>.Success(WindowsServiceSid.RecoveryService);

        internal override Result SetOwner(SafeFileHandle fileHandle, SecurityIdentifier owner)
        {
            SetOwnerCount++;
            Evidence = Evidence with { OwnerSid = owner.Value };
            return Result.Success();
        }

        internal override Result SetDacl(
            SafeFileHandle fileHandle,
            SecurityIdentifier serviceSid)
        {
            SetDaclCount++;
            Evidence = ValidEvidence();
            return Result.Success();
        }
    }

    private sealed class FakePrivileges : IWindowsPrivilegeController
    {
        internal bool FailRevert { get; set; }
        internal int EnableCount { get; private set; }
        internal int RevertCount { get; private set; }

        public Result<IWindowsPrivilegeLease> EnableRestorePrivilege()
        {
            EnableCount++;
            return Result<IWindowsPrivilegeLease>.Success(new Lease(this));
        }

        private sealed class Lease(FakePrivileges owner) : IWindowsPrivilegeLease
        {
            public Result Revert()
            {
                owner.RevertCount++;
                return owner.FailRevert
                    ? Result.Failure(new Error(
                        BrokerErrorCodes.FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED,
                        BrokerErrorCodes.FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED,
                        ErrorCategory.UnrecoverableError))
                    : Result.Success();
            }

            public void Dispose()
            {
            }
        }
    }
}
