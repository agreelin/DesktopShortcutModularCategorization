using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FolderSessionLock.Broker.Recovery;

internal sealed class RecoveryRecordContainer
{
    internal const int HeaderLength = 12;
    internal const int MaximumProtectedPayloadLength = 262144;
    private const ushort ContainerVersion = 1;
    private static ReadOnlySpan<byte> Magic => "FSLR"u8;
    private readonly IRecoveryRecordProtector _protector;

    internal RecoveryRecordContainer()
        : this(new RecoveryRecordProtector())
    {
    }

    internal RecoveryRecordContainer(IRecoveryRecordProtector protector)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    internal byte[] Serialize(RecoveryRecord record)
    {
        byte[] plaintext = RecoveryRecordJson.Serialize(record);
        byte[] protectedPayload = _protector.Protect(plaintext);
        if (protectedPayload.Length is < 1 or > MaximumProtectedPayloadLength)
        {
            throw new InvalidOperationException("The protected recovery payload length is outside the v1 container limit.");
        }

        var container = new byte[checked(HeaderLength + protectedPayload.Length)];
        Magic.CopyTo(container);
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(4, 2), ContainerVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            container.AsSpan(8, 4),
            checked((uint)protectedPayload.Length));
        protectedPayload.CopyTo(container, HeaderLength);
        return container;
    }

    internal RecoveryRecordReadResult Deserialize(ReadOnlySpan<byte> container)
    {
        if (container.Length < HeaderLength)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.Truncated);
        }

        if (!container[..4].SequenceEqual(Magic))
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.MagicInvalid);
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(container[4..6]) != ContainerVersion)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.VersionUnsupported);
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(container[6..8]) != 0)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.FlagsUnsupported);
        }

        uint protectedLength = BinaryPrimitives.ReadUInt32LittleEndian(container[8..12]);
        if (protectedLength is < 1 or > MaximumProtectedPayloadLength)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.LengthInvalid);
        }

        uint totalLength;
        try
        {
            totalLength = checked((uint)HeaderLength + protectedLength);
        }
        catch (OverflowException)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.LengthInvalid);
        }

        if (container.Length < totalLength)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.Truncated);
        }

        if (container.Length > totalLength)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.TrailingData);
        }

        byte[] plaintext;
        try
        {
            plaintext = _protector.Unprotect(container[HeaderLength..].ToArray());
        }
        catch (CryptographicException)
        {
            return RecoveryRecordReadResult.Failure(RecoveryRecordErrors.UnprotectFailed);
        }

        return plaintext.Length > RecoveryRecordJson.MaximumPlaintextLength
            ? RecoveryRecordReadResult.Failure(RecoveryRecordErrors.PayloadTooLarge)
            : RecoveryRecordJson.Deserialize(plaintext);
    }
}
