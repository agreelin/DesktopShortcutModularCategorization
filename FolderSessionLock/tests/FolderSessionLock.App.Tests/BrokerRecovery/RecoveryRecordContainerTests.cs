using System.Buffers.Binary;
using System.Security.Cryptography;
using FolderSessionLock.Broker.Recovery;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class RecoveryRecordContainerTests
{
    [Fact]
    public void Serialize_WritesExactV1HeaderAndRoundTrips()
    {
        var container = new RecoveryRecordContainer(new IdentityProtector());
        RecoveryRecord expected = RecoveryTestData.Prepared();

        byte[] bytes = container.Serialize(expected);
        RecoveryRecordReadResult result = container.Deserialize(bytes);

        Assert.Equal("FSLR"u8.ToArray(), bytes[..4]);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)));
        Assert.Equal((uint)(bytes.Length - 12), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(expected, result.Record);
    }

    [Theory]
    [InlineData(0, "FSL_E_RECOVERY_RECORD_FLAGS_UNSUPPORTED")]
    [InlineData(1, "FSL_E_RECOVERY_RECORD_FLAGS_UNSUPPORTED")]
    [InlineData(15, "FSL_E_RECOVERY_RECORD_FLAGS_UNSUPPORTED")]
    public void Deserialize_RejectsEveryNonZeroFlagsBit(int bit, string code)
    {
        var container = new RecoveryRecordContainer(new IdentityProtector());
        byte[] bytes = container.Serialize(RecoveryTestData.Prepared());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), (ushort)(1 << bit));

        AssertCode(container.Deserialize(bytes), code);
    }

    [Fact]
    public void Deserialize_RejectsAllFlagsSet()
    {
        var container = new RecoveryRecordContainer(new IdentityProtector());
        byte[] bytes = container.Serialize(RecoveryTestData.Prepared());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), ushort.MaxValue);

        AssertCode(container.Deserialize(bytes), "FSL_E_RECOVERY_RECORD_FLAGS_UNSUPPORTED");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(262144)]
    public void Deserialize_AcceptsProtectedPayloadLengthBoundaries(int length)
    {
        var protector = new TrackingProtector(RecoveryRecordJson.Serialize(RecoveryTestData.Prepared()));
        var container = new RecoveryRecordContainer(protector);

        RecoveryRecordReadResult result = container.Deserialize(Build(new byte[length]));

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(1, protector.UnprotectCalls);
        Assert.Equal(length, protector.LastProtectedPayloadLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(262144)]
    public void Serialize_WritesProtectedPayloadLengthBoundaries(int length)
    {
        var container = new RecoveryRecordContainer(new LengthProtector(length));

        byte[] bytes = container.Serialize(RecoveryTestData.Prepared());

        Assert.Equal(length + RecoveryRecordContainer.HeaderLength, bytes.Length);
        Assert.Equal((uint)length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(262145)]
    public void Serialize_RejectsProtectedPayloadLengthsOutsideV1Range(int length)
    {
        var container = new RecoveryRecordContainer(new LengthProtector(length));

        Assert.Throws<InvalidOperationException>(() => container.Serialize(RecoveryTestData.Prepared()));
    }

    [Fact]
    public void Deserialize_RejectsMagicVersionLengthTruncationAndTrailingData()
    {
        var container = new RecoveryRecordContainer(new IdentityProtector());
        byte[] valid = container.Serialize(RecoveryTestData.Prepared());

        byte[] magic = valid.ToArray();
        magic[0] = 0;
        byte[] version = valid.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(version.AsSpan(4, 2), 2);
        byte[] zeroLength = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(zeroLength.AsSpan(8, 4), 0);
        byte[] tooLarge = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(tooLarge.AsSpan(8, 4), 262145);
        byte[] overflow = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(overflow.AsSpan(8, 4), uint.MaxValue);
        byte[] truncated = valid[..^1];
        byte[] trailingZero = [.. valid, 0];
        byte[] trailingNonZero = [.. valid, 1];
        byte[] declaredLarger = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            declaredLarger.AsSpan(8, 4),
            checked((uint)(valid.Length - RecoveryRecordContainer.HeaderLength + 1)));
        byte[] declaredSmaller = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            declaredSmaller.AsSpan(8, 4),
            checked((uint)(valid.Length - RecoveryRecordContainer.HeaderLength - 1)));

        AssertCode(container.Deserialize(valid[..11]), "FSL_E_RECOVERY_RECORD_TRUNCATED");
        AssertCode(container.Deserialize(magic), "FSL_E_RECOVERY_RECORD_MAGIC_INVALID");
        AssertCode(container.Deserialize(version), "FSL_E_RECOVERY_RECORD_VERSION_UNSUPPORTED");
        AssertCode(container.Deserialize(zeroLength), "FSL_E_RECOVERY_RECORD_LENGTH_INVALID");
        AssertCode(container.Deserialize(tooLarge), "FSL_E_RECOVERY_RECORD_LENGTH_INVALID");
        AssertCode(container.Deserialize(overflow), "FSL_E_RECOVERY_RECORD_LENGTH_INVALID");
        AssertCode(container.Deserialize(truncated), "FSL_E_RECOVERY_RECORD_TRUNCATED");
        AssertCode(container.Deserialize(declaredLarger), "FSL_E_RECOVERY_RECORD_TRUNCATED");
        AssertCode(container.Deserialize(declaredSmaller), "FSL_E_RECOVERY_RECORD_TRAILING_DATA");
        AssertCode(container.Deserialize(trailingZero), "FSL_E_RECOVERY_RECORD_TRAILING_DATA");
        AssertCode(container.Deserialize(trailingNonZero), "FSL_E_RECOVERY_RECORD_TRAILING_DATA");
    }

    [Fact]
    public void Deserialize_RejectsHeaderAndLengthFailuresBeforeCallingProtector()
    {
        byte[] valid = new RecoveryRecordContainer(new IdentityProtector())
            .Serialize(RecoveryTestData.Prepared());
        byte[] magic = valid.ToArray();
        magic[0] = 0;
        byte[] version = valid.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(version.AsSpan(4, 2), 2);
        byte[] flags = valid.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(flags.AsSpan(6, 2), 1);
        byte[] zeroLength = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(zeroLength.AsSpan(8, 4), 0);
        byte[] tooLarge = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(tooLarge.AsSpan(8, 4), 262145);
        byte[] declaredLarger = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            declaredLarger.AsSpan(8, 4),
            checked((uint)(valid.Length - RecoveryRecordContainer.HeaderLength + 1)));
        byte[] declaredSmaller = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            declaredSmaller.AsSpan(8, 4),
            checked((uint)(valid.Length - RecoveryRecordContainer.HeaderLength - 1)));

        foreach (byte[] invalid in new[]
                 {
                     valid[..11], magic, version, flags, zeroLength, tooLarge,
                     declaredLarger, declaredSmaller,
                 })
        {
            var protector = new TrackingProtector(RecoveryRecordJson.Serialize(RecoveryTestData.Prepared()));

            Assert.False(new RecoveryRecordContainer(protector).Deserialize(invalid).IsSuccess);
            Assert.Equal(0, protector.UnprotectCalls);
        }
    }

    [Fact]
    public void Deserialize_MapsUnprotectAndPlaintextValidationFailures()
    {
        byte[] protectedPayload = [1];
        byte[] bytes = Build(protectedPayload);

        AssertCode(
            new RecoveryRecordContainer(new ThrowingProtector()).Deserialize(bytes),
            "FSL_E_RECOVERY_RECORD_UNPROTECT_FAILED");
        AssertCode(
            new RecoveryRecordContainer(new FixedPlaintextProtector(new byte[131073])).Deserialize(bytes),
            "FSL_E_RECOVERY_PAYLOAD_TOO_LARGE");
        AssertCode(
            new RecoveryRecordContainer(new FixedPlaintextProtector([0xef, 0xbb, 0xbf, (byte)'{', (byte)'}'])).Deserialize(bytes),
            "FSL_E_RECOVERY_PAYLOAD_MALFORMED");
        AssertCode(
            new RecoveryRecordContainer(new FixedPlaintextProtector([0xff])).Deserialize(bytes),
            "FSL_E_RECOVERY_PAYLOAD_MALFORMED");
        AssertCode(
            new RecoveryRecordContainer(new FixedPlaintextProtector(
                [.. RecoveryRecordJson.Serialize(RecoveryTestData.Prepared()), (byte)'{', (byte)'}']))
                .Deserialize(bytes),
            "FSL_E_RECOVERY_PAYLOAD_MALFORMED");
    }

    [Fact]
    public void DpapiLocalMachine_RoundTripsOnWindows()
    {
        var container = new RecoveryRecordContainer();
        RecoveryRecord expected = RecoveryTestData.Prepared();

        RecoveryRecordReadResult result = container.Deserialize(container.Serialize(expected));

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(expected, result.Record);
    }

    private static byte[] Build(byte[] payload)
    {
        var bytes = new byte[12 + payload.Length];
        "FSLR"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)payload.Length);
        payload.CopyTo(bytes, 12);
        return bytes;
    }

    private static void AssertCode(RecoveryRecordReadResult result, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
    }

    private sealed class IdentityProtector : IRecoveryRecordProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedPayload) => protectedPayload.ToArray();
    }

    private sealed class ThrowingProtector : IRecoveryRecordProtector
    {
        public byte[] Protect(byte[] plaintext) => [1];

        public byte[] Unprotect(byte[] protectedPayload) => throw new CryptographicException();
    }

    private sealed class FixedPlaintextProtector(byte[] plaintext) : IRecoveryRecordProtector
    {
        public byte[] Protect(byte[] value) => [1];

        public byte[] Unprotect(byte[] protectedPayload) => plaintext;
    }

    private sealed class LengthProtector(int length) : IRecoveryRecordProtector
    {
        public byte[] Protect(byte[] plaintext) => new byte[length];

        public byte[] Unprotect(byte[] protectedPayload) => throw new NotSupportedException();
    }

    private sealed class TrackingProtector(byte[] plaintext) : IRecoveryRecordProtector
    {
        internal int UnprotectCalls { get; private set; }

        internal int LastProtectedPayloadLength { get; private set; }

        public byte[] Protect(byte[] value) => value.ToArray();

        public byte[] Unprotect(byte[] protectedPayload)
        {
            UnprotectCalls++;
            LastProtectedPayloadLength = protectedPayload.Length;
            return plaintext;
        }
    }
}
