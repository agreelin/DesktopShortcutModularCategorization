using System.Buffers.Binary;
using System.Security.Cryptography;
using FolderSessionLock.Windows.Models;

namespace FolderSessionLock.Windows.Security;

public sealed record RecoveryAclEvidence(
    string AceFingerprintSha256,
    string BaselineDaclSha256,
    string? PostApplyDaclSha256)
{
    private static ReadOnlySpan<byte> AcePrefix => "FSLACE"u8;
    private static ReadOnlySpan<byte> DaclPrefix => "FSLDACL"u8;

    internal static RecoveryAclEvidence Prepared(
        DirectoryAclSnapshot baseline,
        byte[] expectedAce) =>
        new(
            ComputeAceFingerprint(expectedAce),
            ComputeDaclDigest(baseline),
            null);

    internal RecoveryAclEvidence Applied(
        DirectoryAclSnapshot postApply,
        byte[] actualAce)
    {
        string actualFingerprint = ComputeAceFingerprint(actualAce);
        if (!string.Equals(AceFingerprintSha256, actualFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The applied ACE fingerprint does not match the prepared evidence.");
        }

        return this with { PostApplyDaclSha256 = ComputeDaclDigest(postApply) };
    }

    internal static string ComputeAceFingerprint(ReadOnlySpan<byte> ace)
    {
        ValidateAce(ace);
        var input = new byte[12 + ace.Length];
        AcePrefix.CopyTo(input);
        input[6] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8, 4), checked((uint)ace.Length));
        ace.CopyTo(input.AsSpan(12));
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    internal static string ComputeDaclDigest(DirectoryAclSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int length = checked(20 + snapshot.AceBinaries.Sum(ace => 4 + ace.Length));
        var input = new byte[length];
        DaclPrefix.CopyTo(input);
        input[7] = 1;
        input[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(
            input.AsSpan(10, 2),
            (ushort)((ushort)snapshot.ControlFlags & 0x1504));
        input[12] = snapshot.AclRevision;
        BinaryPrimitives.WriteUInt32LittleEndian(
            input.AsSpan(16, 4),
            checked((uint)snapshot.AceBinaries.Count));

        int offset = 20;
        foreach (byte[] ace in snapshot.AceBinaries)
        {
            ValidateAce(ace);
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(offset, 4), checked((uint)ace.Length));
            offset += 4;
            ace.CopyTo(input, offset);
            offset += ace.Length;
        }

        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static void ValidateAce(ReadOnlySpan<byte> ace)
    {
        if (ace.Length < 4
            || (ace.Length & 3) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(ace[2..4]) != ace.Length)
        {
            throw new ArgumentException("The ACE bytes do not contain a valid ACE_HEADER size.", nameof(ace));
        }
    }
}
