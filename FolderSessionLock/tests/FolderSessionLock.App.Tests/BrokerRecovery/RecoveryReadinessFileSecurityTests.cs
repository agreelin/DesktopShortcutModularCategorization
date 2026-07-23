using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class RecoveryReadinessFileSecurityTests
{
    [Fact]
    public Task VerifyAsync_DirectoryAcceptsTheExactUsersReadAndExecutePolicy() =>
        VerifyExactPolicyAsync(RecoveryReadinessObjectKind.Directory, 0x001200A9, 4);

    [Fact]
    public Task VerifyAsync_CanonicalAcceptsTheExactUsersReadPolicy() =>
        VerifyExactPolicyAsync(RecoveryReadinessObjectKind.CanonicalFile, 0x00120089, 1);

    [Fact]
    public Task VerifyAsync_TemporaryAcceptsTheExactUsersReadPolicy() =>
        VerifyExactPolicyAsync(RecoveryReadinessObjectKind.TemporaryFile, 0x00120089, 1);

    private static async Task VerifyExactPolicyAsync(
        RecoveryReadinessObjectKind kind,
        int usersMask,
        uint links)
    {
        var platform = new FakePlatform(ValidEvidence(usersMask, links));
        var security = new RecoveryReadinessFileSecurity(platform, new FakePrivileges());
        using var handle = new SafeFileHandle(new nint(1), ownsHandle: false);

        Result<RecoveryRecordFileIdentity> result = await security.VerifyAsync(
            handle,
            kind,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(platform.Evidence.Identity, result.Value);
        Assert.Equal(ProtectedPathAclPolicy.UsersSid, platform.Evidence.Aces[3].Sid);
        Assert.Equal(usersMask, platform.Evidence.Aces[3].AccessMask);
    }

    [Fact]
    public async Task VerifyAsync_RejectsOwnerDaclInheritanceAceAndLinkViolations()
    {
        RecoveryRecordFileSecurityEvidence valid = ValidEvidence(0x00120089, 1);
        RecoveryRecordFileSecurityEvidence[] invalid =
        [
            valid with { OwnerSid = ProtectedPathAclPolicy.AdministratorsSid },
            valid with { DaclPresent = false },
            valid with { DaclIsNull = true },
            valid with { DaclProtected = false },
            valid with { AclRevision = 4 },
            valid with { Aces = valid.Aces.Take(3).ToArray() },
            valid with { Aces = [.. valid.Aces, valid.Aces[0]] },
            valid with
            {
                Aces = valid.Aces.Select((ace, index) =>
                    index == 3 ? ace with { AceFlags = AceFlags.Inherited } : ace).ToArray(),
            },
            valid with
            {
                Aces = valid.Aces.Select((ace, index) =>
                    index == 3 ? ace with { AccessMask = 0x001F01FF } : ace).ToArray(),
            },
            valid with { Identity = valid.Identity with { NumberOfLinks = 2 } },
        ];
        using var handle = new SafeFileHandle(new nint(1), ownsHandle: false);
        foreach (RecoveryRecordFileSecurityEvidence evidence in invalid)
        {
            var security = new RecoveryReadinessFileSecurity(
                new FakePlatform(evidence),
                new FakePrivileges());

            Result<RecoveryRecordFileIdentity> result = await security.VerifyAsync(
                handle,
                RecoveryReadinessObjectKind.CanonicalFile,
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(
                BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
                result.Error!.Code);
        }
    }

    [Fact]
    public async Task ApplyAndVerifyAsync_AllowsOnlyTemporaryAndRestoresPrivilege()
    {
        var platform = new FakePlatform(ValidEvidence(0x00120089, 1) with
        {
            OwnerSid = ProtectedPathAclPolicy.AdministratorsSid,
        });
        var privileges = new FakePrivileges();
        var security = new RecoveryReadinessFileSecurity(platform, privileges);
        using var handle = new SafeFileHandle(new nint(1), ownsHandle: false);

        Result<RecoveryRecordFileIdentity> result = await security.ApplyAndVerifyAsync(
            handle,
            RecoveryReadinessObjectKind.TemporaryFile,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, privileges.EnableCount);
        Assert.Equal(1, privileges.RevertCount);
        Assert.Equal(1, platform.SetOwnerCount);
        Assert.Equal(1, platform.SetDaclCount);
        Assert.True((await security.ApplyAndVerifyAsync(
            handle,
            RecoveryReadinessObjectKind.CanonicalFile,
            CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task ApplyAndVerifyAsync_PrivilegeRevertFailureFailsClosed()
    {
        var platform = new FakePlatform(ValidEvidence(0x00120089, 1) with
        {
            OwnerSid = ProtectedPathAclPolicy.AdministratorsSid,
        });
        var security = new RecoveryReadinessFileSecurity(
            platform,
            new FakePrivileges { FailRevert = true });
        using var handle = new SafeFileHandle(new nint(1), ownsHandle: false);

        Result<RecoveryRecordFileIdentity> result = await security.ApplyAndVerifyAsync(
            handle,
            RecoveryReadinessObjectKind.TemporaryFile,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
            result.Error!.Code);
    }

    private static RecoveryRecordFileSecurityEvidence ValidEvidence(int usersMask, uint links)
    {
        string serviceSid = WindowsServiceSid.RecoveryService.Value;
        return new RecoveryRecordFileSecurityEvidence(
            new RecoveryRecordFileIdentity(1, 2, 3, links),
            ProtectedPathAclPolicy.SystemSid,
            true,
            false,
            true,
            2,
            [
                Ace(ProtectedPathAclPolicy.SystemSid, 0x001F01FF),
                Ace(ProtectedPathAclPolicy.AdministratorsSid, 0x001F01FF),
                Ace(serviceSid, 0x001F01FF),
                Ace(ProtectedPathAclPolicy.UsersSid, usersMask),
            ]);
    }

    private static RecoveryRecordFileAce Ace(string sid, int mask) => new(
        AceType.AccessAllowed,
        AceFlags.None,
        mask,
        sid,
        AceQualifier.AccessAllowed,
        false,
        false,
        true);

    private sealed class FakePlatform(RecoveryRecordFileSecurityEvidence evidence)
        : WindowsRecoveryRecordFileSecurityPlatform
    {
        internal RecoveryRecordFileSecurityEvidence Evidence { get; private set; } = evidence;
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
            Evidence = ValidEvidence(0x00120089, 1);
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
