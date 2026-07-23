namespace FolderSessionLock.Protocol;

public static class BrokerProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const int HandshakeVersion = 1;
    public const string UtcTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    public const string GuidFormat = "D";
    public const int MinimumPathLength = 1;
    public const int MaximumPathLength = 32767;
    public const int MaximumErrorMessageLength = 256;
    public const int NonceByteLength = 32;
    public const string PipeName = "FolderSessionLock.Broker.v1";
    public const string ClientHello = "ClientHello";
    public const string ServerHello = "ServerHello";
    public const string CommandRequest = "CommandRequest";
    public const string CommandResponse = "CommandResponse";
    public const string ValidatePath = "ValidatePath";
    public const string CreateLock = "CreateLock";
    public const string RemoveLock = "RemoveLock";
    public const string GetStatus = "GetStatus";

    public static IReadOnlyList<string> Commands { get; } = Array.AsReadOnly(
        [ValidatePath, CreateLock, RemoveLock, GetStatus]);
}
