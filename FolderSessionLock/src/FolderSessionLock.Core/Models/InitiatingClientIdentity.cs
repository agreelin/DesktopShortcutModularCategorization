namespace FolderSessionLock.Core.Models;

public sealed record InitiatingClientIdentity
{
    public InitiatingClientIdentity(
        uint processId,
        ulong processCreationFileTime,
        string accountSid,
        string logonSid,
        uint windowsSessionId)
    {
        if (processId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(accountSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(logonSid);
        ProcessId = processId;
        ProcessCreationFileTime = processCreationFileTime;
        AccountSid = accountSid;
        LogonSid = logonSid;
        WindowsSessionId = windowsSessionId;
    }

    public uint ProcessId { get; }
    public ulong ProcessCreationFileTime { get; }
    public string AccountSid { get; }
    public string LogonSid { get; }
    public uint WindowsSessionId { get; }
}
