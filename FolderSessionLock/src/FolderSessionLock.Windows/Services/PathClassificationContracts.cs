using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Models;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Services;

public interface IRepositoryRootClassifier
{
    Result<bool> IsUnderRepositoryRoot(ValidatedDirectory directory);
}

public interface ISynchronizationRootClassifier
{
    Result<bool> IsUnderSynchronizationRoot(ValidatedDirectory directory);
}

internal interface IInitiatingUserTokenSource
{
    Result<SafeAccessTokenHandle> GetToken();
}
