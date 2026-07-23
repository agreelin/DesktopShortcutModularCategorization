using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Windows.Security;

internal sealed class ProtectedPathAclPolicy
{
    internal static readonly string SystemSid =
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
    internal static readonly string AdministratorsSid =
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
    internal static readonly string UsersSid =
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;
    internal static readonly string AuthenticatedUsersSid =
        new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value;
    internal static readonly string EveryoneSid =
        new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value;
    internal const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    internal static readonly string RecoveryServiceSid = WindowsServiceSid.RecoveryService.Value;

    private const int InheritanceMask =
        (int)(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
    private const int InstallOrdinaryRiskMask = (int)(
        FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.WriteAttributes
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership);
    private const int RecoveryOrdinaryRiskMask = InstallOrdinaryRiskMask | (int)(
        FileSystemRights.ReadData
        | FileSystemRights.ReadAttributes
        | FileSystemRights.ReadExtendedAttributes
        | FileSystemRights.ReadPermissions
        | FileSystemRights.ExecuteFile);

    internal string? Validate(
        ProtectedPathKind pathKind,
        ProtectedPathSecurityDescriptor descriptor)
    {
        if (!AllowedOwners(pathKind).Contains(descriptor.OwnerSid, StringComparer.Ordinal))
        {
            return BrokerErrorCodes.FSL_E_PROTECTED_PATH_OWNER_MISMATCH;
        }

        bool isProtectedStorage = pathKind != ProtectedPathKind.InstallDirectory;
        if (isProtectedStorage
            && (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
        {
            return BrokerErrorCodes.FSL_E_PROTECTED_PATH_INHERITANCE_INVALID;
        }

        if (isProtectedStorage && descriptor.Aces.Any(ace => ace.IsInherited))
        {
            return BrokerErrorCodes.FSL_E_PROTECTED_PATH_INHERITANCE_INVALID;
        }

        IReadOnlyList<RequiredAce> required = RequiredAces(pathKind);
        int riskMask = isProtectedStorage ? RecoveryOrdinaryRiskMask : InstallOrdinaryRiskMask;
        if (descriptor.Aces.Any(ace => !IsOrdinaryAllow(ace)))
        {
            return BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH;
        }

        foreach (RequiredAce expected in required)
        {
            if (!descriptor.Aces.Any(ace => MatchesRequired(ace, expected)))
            {
                return BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH;
            }
        }

        foreach (ProtectedPathAce ace in descriptor.Aces.Where(ace => ace.IsAllow))
        {
            if (isProtectedStorage
                && !required.Any(requiredAce => requiredAce.Sid == ace.Sid))
            {
                return BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH;
            }

            if (IsOrdinaryPrincipal(ace.Sid) && (ace.AccessMask & riskMask) != 0)
            {
                return BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH;
            }

            if (!IsKnownPrincipal(pathKind, ace.Sid) && (ace.AccessMask & riskMask) != 0)
            {
                return BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH;
            }
        }

        return null;
    }

    internal string? ValidateRecoveryRecordFile(ProtectedPathSecurityDescriptor descriptor)
    {
        if (descriptor.OwnerSid != SystemSid
            || !descriptor.DaclPresent
            || descriptor.DaclNull)
        {
            return BrokerErrorCodes.FSL_E_RECOVERY_RECORD_MISMATCH;
        }

        IReadOnlyList<RequiredAce> required = RequiredAces(ProtectedPathKind.RecoveryRecordsDirectory);
        foreach (RequiredAce expected in required)
        {
            if (!descriptor.Aces.Any(ace =>
                    IsOrdinaryAllow(ace)
                    && ace.Sid == expected.Sid
                    && ace.AccessMask == expected.AccessMask))
            {
                return BrokerErrorCodes.FSL_E_RECOVERY_RECORD_MISMATCH;
            }
        }

        foreach (ProtectedPathAce ace in descriptor.Aces)
        {
            if (!IsOrdinaryAllow(ace)
                || !required.Any(requiredAce => requiredAce.Sid == ace.Sid)
                || ace.AccessMask != (int)FileSystemRights.FullControl)
            {
                return BrokerErrorCodes.FSL_E_RECOVERY_RECORD_MISMATCH;
            }
        }

        return null;
    }

    private static bool MatchesRequired(ProtectedPathAce ace, RequiredAce expected) =>
        IsOrdinaryExplicitAllow(ace)
        && ace.Sid == expected.Sid
        && ace.AccessMask == expected.AccessMask
        && (int)ace.InheritanceFlags == InheritanceMask
        && ace.PropagationFlags == PropagationFlags.None;

    private static bool IsOrdinaryExplicitAllow(ProtectedPathAce ace) =>
        IsOrdinaryAllow(ace)
        && !ace.IsInherited
        && ace.AceFlags == (AceFlags.ContainerInherit | AceFlags.ObjectInherit);

    private static bool IsOrdinaryAllow(ProtectedPathAce ace) =>
        ace.IsQualified
        && ace.IsAllow
        && ace.AceType == AceType.AccessAllowed
        && ace.AceQualifier == AceQualifier.AccessAllowed
        && !ace.IsCallback
        && !ace.IsObjectAce;

    private static IReadOnlyList<string> AllowedOwners(ProtectedPathKind pathKind) =>
        pathKind == ProtectedPathKind.InstallDirectory
            ? [SystemSid, TrustedInstallerSid]
            : [SystemSid];

    private static IReadOnlyList<RequiredAce> RequiredAces(ProtectedPathKind pathKind) =>
        pathKind == ProtectedPathKind.InstallDirectory
            ?
            [
                new(SystemSid, (int)FileSystemRights.FullControl),
                new(AdministratorsSid, (int)FileSystemRights.FullControl),
                new(UsersSid, (int)FileSystemRights.ReadAndExecute),
            ]
            :
            [
                new(SystemSid, (int)FileSystemRights.FullControl),
                new(AdministratorsSid, (int)FileSystemRights.FullControl),
                new(RecoveryServiceSid, (int)FileSystemRights.FullControl),
            ];

    private static bool IsOrdinaryPrincipal(string sid) =>
        sid == UsersSid || sid == AuthenticatedUsersSid || sid == EveryoneSid;

    private static bool IsKnownPrincipal(ProtectedPathKind pathKind, string sid) =>
        sid == SystemSid
        || sid == AdministratorsSid
        || sid == TrustedInstallerSid
        || sid == UsersSid
        || sid == AuthenticatedUsersSid
        || sid == EveryoneSid
        || (pathKind != ProtectedPathKind.InstallDirectory && sid == RecoveryServiceSid);

    private sealed record RequiredAce(string Sid, int AccessMask);
}

internal sealed record ProtectedPathSecurityDescriptor(
    string OwnerSid,
    bool DaclPresent,
    bool DaclNull,
    ControlFlags ControlFlags,
    IReadOnlyList<ProtectedPathAce> Aces);

internal sealed record ProtectedPathAce(
    bool IsAllow,
    string Sid,
    int AccessMask,
    InheritanceFlags InheritanceFlags,
    PropagationFlags PropagationFlags,
    bool IsInherited,
    AceType AceType = AceType.AccessAllowed,
    AceQualifier? AceQualifier = AceQualifier.AccessAllowed,
    bool IsCallback = false,
    bool IsObjectAce = false,
    bool IsQualified = true,
    AceFlags AceFlags = AceFlags.ContainerInherit | AceFlags.ObjectInherit);
