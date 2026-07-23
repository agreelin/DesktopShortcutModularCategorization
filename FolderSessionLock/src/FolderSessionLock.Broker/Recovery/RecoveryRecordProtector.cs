using System.Security.Cryptography;
using System.Text;

namespace FolderSessionLock.Broker.Recovery;

internal interface IRecoveryRecordProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedPayload);
}

internal sealed class RecoveryRecordProtector : IRecoveryRecordProtector
{
    private const string Purpose = "FolderSessionLock.RecoveryRecord.v1";
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes(Purpose));

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(
        plaintext,
        Entropy,
        DataProtectionScope.LocalMachine);

    public byte[] Unprotect(byte[] protectedPayload) => ProtectedData.Unprotect(
        protectedPayload,
        Entropy,
        DataProtectionScope.LocalMachine);
}
