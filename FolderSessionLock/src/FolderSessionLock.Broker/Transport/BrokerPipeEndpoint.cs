using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Transport;

public static class BrokerPipeEndpoint
{
    public const string PipeName = BrokerProtocolConstants.PipeName;
    public const string LocalPath = @"\\.\pipe\FolderSessionLock.Broker.v1";
    public const int LengthPrefixSize = sizeof(uint);
    public const int MinimumBodyLength = 1;
    public const int MaximumBodyLength = 65_536;

    public static void EnsureFixedName(string pipeName)
    {
        ArgumentNullException.ThrowIfNull(pipeName);
        if (!string.Equals(pipeName, PipeName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The broker pipe name is not supported.", nameof(pipeName));
        }
    }
}
