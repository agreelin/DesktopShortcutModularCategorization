using System.Security.AccessControl;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;

namespace FolderSessionLock.Windows.Tests.Security;

public sealed class RecoveryAclEvidenceTests
{
    private const string DenyAceHex =
        "01031c00890012000103000000000005050000000100000002000000";
    private const string AllowAceHex =
        "00031400ff011f00010100000000000512000000";

    [Fact]
    public void FixedD022Vectors_ProduceExactDigests()
    {
        byte[] denyAce = Convert.FromHexString(DenyAceHex);
        byte[] allowAce = Convert.FromHexString(AllowAceHex);
        DirectoryAclSnapshot baseline = Snapshot([allowAce]);
        DirectoryAclSnapshot postApply = Snapshot([denyAce, allowAce]);

        Assert.Equal(
            "366092caef8b4ccd9a05728cc017b2b155a9f8aa74358e6df901e0554a8239f7",
            RecoveryAclEvidence.ComputeAceFingerprint(denyAce));
        Assert.Equal(
            "62fffcf46d188397e84da5b800129f54cacc87fe86ef9ca1f9eac9c6eef2db17",
            RecoveryAclEvidence.ComputeDaclDigest(baseline));
        Assert.Equal(
            "0bd878690d59d8de240e84199560b65db09c2f473dffc717aabb75642566f026",
            RecoveryAclEvidence.ComputeDaclDigest(postApply));
    }

    [Fact]
    public void DaclDigest_PreservesAceOrderAndIgnoresOwnerAndGroup()
    {
        byte[] denyAce = Convert.FromHexString(DenyAceHex);
        byte[] allowAce = Convert.FromHexString(AllowAceHex);
        DirectoryAclSnapshot expected = Snapshot([denyAce, allowAce]);
        var changedOwner = new DirectoryAclSnapshot(
            "S-1-5-21-1-2-3-4",
            "S-1-5-32-544",
            expected.ControlFlags,
            expected.AclRevision,
            [9, 9, 9],
            expected.AceBinaries);
        DirectoryAclSnapshot reversed = Snapshot([allowAce, denyAce]);

        Assert.Equal(
            RecoveryAclEvidence.ComputeDaclDigest(expected),
            RecoveryAclEvidence.ComputeDaclDigest(changedOwner));
        Assert.NotEqual(
            RecoveryAclEvidence.ComputeDaclDigest(expected),
            RecoveryAclEvidence.ComputeDaclDigest(reversed));
    }

    [Fact]
    public void AceFingerprint_IsSensitiveToTypeFlagsMaskAndSid()
    {
        byte[] expected = Convert.FromHexString(DenyAceHex);
        string fingerprint = RecoveryAclEvidence.ComputeAceFingerprint(expected);

        foreach (int offset in new[] { 0, 1, 4, 12 })
        {
            byte[] changed = expected.ToArray();
            changed[offset] ^= 0x01;

            Assert.NotEqual(fingerprint, RecoveryAclEvidence.ComputeAceFingerprint(changed));
        }
    }

    [Fact]
    public void DaclDigest_IsSensitiveToControlRevisionAndEffectiveAceBytes()
    {
        byte[] allowAce = Convert.FromHexString(AllowAceHex);
        DirectoryAclSnapshot expected = Snapshot([allowAce]);
        byte[] changedAce = allowAce.ToArray();
        changedAce[4] ^= 0x01;
        var changedControl = new DirectoryAclSnapshot(
            expected.OwnerSid,
            expected.GroupSid,
            expected.ControlFlags | ControlFlags.DiscretionaryAclProtected,
            expected.AclRevision,
            expected.DaclBinary,
            expected.AceBinaries);
        var changedRevision = new DirectoryAclSnapshot(
            expected.OwnerSid,
            expected.GroupSid,
            expected.ControlFlags,
            4,
            expected.DaclBinary,
            expected.AceBinaries);

        string digest = RecoveryAclEvidence.ComputeDaclDigest(expected);
        Assert.NotEqual(digest, RecoveryAclEvidence.ComputeDaclDigest(changedControl));
        Assert.NotEqual(digest, RecoveryAclEvidence.ComputeDaclDigest(changedRevision));
        Assert.NotEqual(digest, RecoveryAclEvidence.ComputeDaclDigest(Snapshot([changedAce])));
    }

    [Fact]
    public void DaclDigest_IgnoresSaclSelfRelativeAndUnusedAclTail()
    {
        byte[] allowAce = Convert.FromHexString(AllowAceHex);
        DirectoryAclSnapshot expected = Snapshot([allowAce]);
        var ignoredChanges = new DirectoryAclSnapshot(
            expected.OwnerSid,
            expected.GroupSid,
            expected.ControlFlags | ControlFlags.SystemAclPresent | ControlFlags.SelfRelative,
            expected.AclRevision,
            [.. expected.DaclBinary, 9, 8, 7, 6],
            expected.AceBinaries);

        Assert.Equal(
            RecoveryAclEvidence.ComputeDaclDigest(expected),
            RecoveryAclEvidence.ComputeDaclDigest(ignoredChanges));
    }

    [Fact]
    public void DaclPresenceValidation_RejectsMissingAndNullDacl()
    {
        Assert.False(DirectoryAclEditor.HasPresentNonNullDacl(ControlFlags.None, (nint)1));
        Assert.False(DirectoryAclEditor.HasPresentNonNullDacl(
            ControlFlags.DiscretionaryAclPresent,
            nint.Zero));
        Assert.True(DirectoryAclEditor.HasPresentNonNullDacl(
            ControlFlags.DiscretionaryAclPresent,
            (nint)1));
    }

    [Fact]
    public void AceFingerprint_RejectsInvalidAceHeaderSize()
    {
        byte[] invalid = Convert.FromHexString(DenyAceHex);
        invalid[2] = 0x02;

        Assert.Throws<ArgumentException>(() => RecoveryAclEvidence.ComputeAceFingerprint(invalid));
    }

    [Fact]
    public void AceFingerprint_RejectsAceSizeThatDoesNotMatchActualLength()
    {
        byte[] invalid = Convert.FromHexString(DenyAceHex);
        invalid[2] = 0x18;

        Assert.Throws<ArgumentException>(() => RecoveryAclEvidence.ComputeAceFingerprint(invalid));
    }

    private static DirectoryAclSnapshot Snapshot(IReadOnlyList<byte[]> aces) =>
        new(
            "S-1-5-18",
            "S-1-5-18",
            ControlFlags.DiscretionaryAclPresent,
            2,
            [0],
            aces);
}
