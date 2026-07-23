namespace FolderSessionLock.Windows.Tests.Infrastructure;

internal static class AclTestSafetyGate
{
    private static Exception? _failure;

    internal static void EnsureCanWrite()
    {
        if (_failure is not null)
        {
            throw new InvalidOperationException(
                "A previous ACL cleanup failed; further ACL writes are blocked.",
                _failure);
        }
    }

    internal static void Block(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Interlocked.CompareExchange(ref _failure, failure, null);
    }

    internal static Exception? CaptureFailure() => Volatile.Read(ref _failure);

    internal static void RestoreFailure(Exception? failure) => Volatile.Write(ref _failure, failure);
}
