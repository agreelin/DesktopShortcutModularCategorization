using System.Security.AccessControl;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Windows.Tests.Security;

public sealed class ProtectedPathAclPolicyTests
{
    [Fact]
    public void RecoveryPolicy_RequiresProtectedExactExplicitAces()
    {
        var policy = new ProtectedPathAclPolicy();
        ProtectedPathSecurityDescriptor valid = RecoveryDescriptor();

        Assert.Null(policy.Validate(ProtectedPathKind.RecoveryRecordsDirectory, valid));
        Assert.Equal(
            BrokerErrorCodes.FSL_E_PROTECTED_PATH_INHERITANCE_INVALID,
            policy.Validate(
                ProtectedPathKind.RecoveryRecordsDirectory,
                valid with { ControlFlags = ControlFlags.DiscretionaryAclPresent }));
        Assert.Equal(
            BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH,
            policy.Validate(
                ProtectedPathKind.RecoveryRecordsDirectory,
                valid with { Aces = valid.Aces.Skip(1).ToArray() }));
        Assert.Equal(
            BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH,
            policy.Validate(
                ProtectedPathKind.RecoveryRecordsDirectory,
                valid with
                {
                    Aces = valid.Aces.Append(new ProtectedPathAce(
                        true,
                        ProtectedPathAclPolicy.UsersSid,
                        (int)FileSystemRights.ReadData,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        false)).ToArray(),
                }));
    }

    [Fact]
    public void RecoveryPolicy_RejectsConflictingDenyAndNonOrdinaryHighRiskAces()
    {
        var policy = new ProtectedPathAclPolicy();
        ProtectedPathSecurityDescriptor valid = RecoveryDescriptor();

        foreach (ProtectedPathAce ace in new[]
                 {
                     NonOrdinaryAce(
                         AceType.AccessDenied,
                         AceQualifier.AccessDenied,
                         isCallback: false,
                         isObjectAce: false),
                     NonOrdinaryAce(
                         AceType.AccessAllowedCallback,
                         AceQualifier.AccessAllowed,
                         isCallback: true,
                         isObjectAce: false),
                     NonOrdinaryAce(
                         AceType.AccessAllowedObject,
                         AceQualifier.AccessAllowed,
                         isCallback: false,
                         isObjectAce: true),
                     NonOrdinaryAce(
                         AceType.SystemAudit,
                         AceQualifier.SystemAudit,
                         isCallback: false,
                         isObjectAce: false),
                 })
        {
            Assert.Equal(
                BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH,
                policy.Validate(
                    ProtectedPathKind.RecoveryRecordsDirectory,
                    valid with { Aces = valid.Aces.Append(ace).ToArray() }));
        }
    }

    public static TheoryData<string> InstallOwners => new()
    {
        ProtectedPathAclPolicy.SystemSid,
        ProtectedPathAclPolicy.TrustedInstallerSid,
    };

    [Theory]
    [MemberData(nameof(InstallOwners))]
    public void InstallPolicy_AllowsOnlyTheDocumentedOwners(string ownerSid)
    {
        var policy = new ProtectedPathAclPolicy();
        ProtectedPathSecurityDescriptor descriptor = InstallDescriptor(ownerSid);

        Assert.Null(policy.Validate(ProtectedPathKind.InstallDirectory, descriptor));
        Assert.Equal(
            BrokerErrorCodes.FSL_E_PROTECTED_PATH_OWNER_MISMATCH,
            policy.Validate(
                ProtectedPathKind.InstallDirectory,
                descriptor with { OwnerSid = ProtectedPathAclPolicy.UsersSid }));
    }

    internal static ProtectedPathSecurityDescriptor RecoveryDescriptor() => new(
        ProtectedPathAclPolicy.SystemSid,
        true,
        false,
        ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
        [
            FullControl(ProtectedPathAclPolicy.SystemSid),
            FullControl(ProtectedPathAclPolicy.AdministratorsSid),
            FullControl(ProtectedPathAclPolicy.RecoveryServiceSid),
        ]);

    private static ProtectedPathSecurityDescriptor InstallDescriptor(string ownerSid) => new(
        ownerSid,
        true,
        false,
        ControlFlags.DiscretionaryAclPresent,
        [
            FullControl(ProtectedPathAclPolicy.SystemSid),
            FullControl(ProtectedPathAclPolicy.AdministratorsSid),
            new ProtectedPathAce(
                true,
                ProtectedPathAclPolicy.UsersSid,
                (int)FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                false),
        ]);

    private static ProtectedPathAce FullControl(string sid) => new(
        true,
        sid,
        (int)FileSystemRights.FullControl,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        false);

    private static ProtectedPathAce NonOrdinaryAce(
        AceType aceType,
        AceQualifier aceQualifier,
        bool isCallback,
        bool isObjectAce) => new(
            aceQualifier == AceQualifier.AccessAllowed,
            ProtectedPathAclPolicy.RecoveryServiceSid,
            (int)FileSystemRights.WriteData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            false,
            aceType,
            aceQualifier,
            isCallback,
            isObjectAce,
            true,
            AceFlags.ContainerInherit | AceFlags.ObjectInherit);
}
