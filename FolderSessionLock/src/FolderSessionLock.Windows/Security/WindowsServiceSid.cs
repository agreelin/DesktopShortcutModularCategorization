using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace FolderSessionLock.Windows.Security;

internal static class WindowsServiceSid
{
    internal const string RecoveryServiceName = "FolderSessionLockRecovery";

    internal static SecurityIdentifier RecoveryService { get; } = Create(RecoveryServiceName);

    internal static SecurityIdentifier Create(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        byte[] digest = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        uint[] authorities = new uint[5];
        for (int index = 0; index < authorities.Length; index++)
        {
            authorities[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                digest.AsSpan(index * sizeof(uint)));
        }

        return new SecurityIdentifier($"S-1-5-80-{string.Join('-', authorities)}");
    }
}
