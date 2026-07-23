using System.Buffers.Binary;
using System.Globalization;

namespace FolderSessionLock.Windows.Models;

public readonly record struct DirectoryIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdHigh,
    ulong FileIdLow)
{
    public static DirectoryIdentity FromFileId(
        ulong volumeSerialNumber,
        ReadOnlySpan<byte> fileId128)
    {
        if (fileId128.Length != 16)
        {
            throw new ArgumentException("A FILE_ID_128 value must contain exactly 16 bytes.", nameof(fileId128));
        }

        return new DirectoryIdentity(
            volumeSerialNumber,
            BinaryPrimitives.ReadUInt64LittleEndian(fileId128[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(fileId128[..8]));
    }

    public string VolumeSerialNumberText =>
        VolumeSerialNumber.ToString("x16", CultureInfo.InvariantCulture);

    public string FileIdHighText =>
        FileIdHigh.ToString(CultureInfo.InvariantCulture);

    public string FileIdLowText =>
        FileIdLow.ToString(CultureInfo.InvariantCulture);

    public string FileId128 => Convert.ToHexString(GetFileIdBytes()).ToLowerInvariant();

    public byte[] GetFileIdBytes()
    {
        var identifier = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(identifier.AsSpan(0, 8), FileIdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(identifier.AsSpan(8, 8), FileIdHigh);
        return identifier;
    }
}
