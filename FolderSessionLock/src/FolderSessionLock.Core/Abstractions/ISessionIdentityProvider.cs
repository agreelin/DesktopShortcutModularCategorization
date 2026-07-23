using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Abstractions;

public interface ISessionIdentityProvider
{
    ValueTask<Result<SessionIdentity>> GetCurrentAsync(
        CancellationToken cancellationToken = default);
}
