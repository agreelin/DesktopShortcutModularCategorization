namespace FolderSessionLock.Core.Models;

public sealed record SessionIdentity(string AccountSid, string LogonSid, int WindowsSessionId);
