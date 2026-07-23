using System.Security.AccessControl;

namespace FolderSessionLock.Windows.Models;

public sealed class DirectoryAclSnapshot
{
    internal DirectoryAclSnapshot(
        string ownerSid,
        string groupSid,
        ControlFlags controlFlags,
        byte aclRevision,
        byte[] daclBinary,
        IReadOnlyList<byte[]> aceBinaries)
    {
        OwnerSid = ownerSid;
        GroupSid = groupSid;
        ControlFlags = controlFlags;
        AclRevision = aclRevision;
        DaclBinary = daclBinary;
        AceBinaries = aceBinaries;
        AceCounts = aceBinaries
            .GroupBy(Convert.ToHexString, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    public string OwnerSid { get; }

    public string GroupSid { get; }

    public ControlFlags ControlFlags { get; }

    public byte AclRevision { get; }

    public bool IsProtected =>
        (ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0;

    public bool IsAutoInherited =>
        (ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) != 0;

    public byte[] DaclBinary { get; }

    public IReadOnlyList<byte[]> AceBinaries { get; }

    public IReadOnlyDictionary<string, int> AceCounts { get; }
}
