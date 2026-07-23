using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Windows.Services;

public sealed class UnavailableSessionIdentityProvider : ISessionIdentityProvider
{
    private static readonly Error NotImplementedError = new(
        "windows.session_identity.not_implemented",
        "Windows session identity discovery is not implemented in stage 1.",
        ErrorCategory.PlatformError);

    public ValueTask<Result<SessionIdentity>> GetCurrentAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Result<SessionIdentity>.Failure(NotImplementedError));
}
