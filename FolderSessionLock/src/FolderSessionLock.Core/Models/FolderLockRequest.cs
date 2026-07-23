namespace FolderSessionLock.Core.Models;

public sealed record FolderLockRequest(Guid TaskId, string FolderPath, TimeSpan Duration);
