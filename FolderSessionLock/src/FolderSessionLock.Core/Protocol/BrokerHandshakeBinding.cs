using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FolderSessionLock.Protocol;

public static class BrokerHandshakeBinding
{
    public static string CreateNonce()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(BrokerProtocolConstants.NonceByteLength);
        return Base64UrlEncode(bytes);
    }

    public static bool IsValidNonce(string? value) =>
        TryBase64UrlDecode(value, out byte[] bytes)
        && bytes.Length == BrokerProtocolConstants.NonceByteLength
        && bytes.Any(static value => value != 0);

    public static string CreateProof(
        Guid requestId,
        BrokerCommand command,
        Guid connectionId,
        string clientNonce,
        string serverNonce,
        uint clientSessionId)
    {
        string canonical = string.Join(
            '\n',
            "FSL-BIND-V1",
            requestId.ToString("D"),
            command.ToString(),
            connectionId.ToString("D"),
            clientNonce,
            serverNonce,
            clientSessionId.ToString(CultureInfo.InvariantCulture));
        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool VerifyProof(
        string? proof,
        Guid requestId,
        BrokerCommand command,
        Guid connectionId,
        string clientNonce,
        string serverNonce,
        uint clientSessionId)
    {
        if (!TryBase64UrlDecode(proof, out byte[] actual) || actual.Length != 32)
        {
            return false;
        }

        string expected = CreateProof(
            requestId,
            command,
            connectionId,
            clientNonce,
            serverNonce,
            clientSessionId);
        TryBase64UrlDecode(expected, out byte[] expectedBytes);
        return CryptographicOperations.FixedTimeEquals(actual, expectedBytes);
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryBase64UrlDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value)
            || value.Contains('=')
            || value.Any(static character =>
                character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-'
                and not '_'))
        {
            return false;
        }

        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => "!",
        };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return string.Equals(Base64UrlEncode(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
