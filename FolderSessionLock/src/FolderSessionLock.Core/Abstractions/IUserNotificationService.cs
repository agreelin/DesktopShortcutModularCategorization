using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Abstractions;

public interface IUserNotificationService
{
    ValueTask<Result> NotifyAsync(
        UserNotification notification,
        CancellationToken cancellationToken = default);
}
