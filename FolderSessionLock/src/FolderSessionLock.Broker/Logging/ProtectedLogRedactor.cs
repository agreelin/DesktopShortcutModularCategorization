using System.Security.Cryptography;
using System.Text;

namespace FolderSessionLock.Broker.Logging;

public static class ProtectedLogRedactor
{
    private const string PathDomain = "FSL-PATH-LOG-V1\n";

    public static string HashPath(string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);
        byte[] bytes = Encoding.UTF8.GetBytes(PathDomain + normalizedPath);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
