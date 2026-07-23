namespace FolderSessionLock.Windows.Tests.Infrastructure;

public sealed class TemporaryTestDirectory : IDisposable, IAsyncDisposable
{
    private readonly Action<string> _deleteDirectory;
    private bool _disposed;

    private TemporaryTestDirectory(string path, Action<string> deleteDirectory)
    {
        Path = path;
        _deleteDirectory = deleteDirectory;
    }

    public string Path { get; }

    public static TemporaryTestDirectory Create()
        => Create(path => Directory.Delete(path, recursive: true));

    internal static TemporaryTestDirectory Create(Action<string> deleteDirectory)
    {
        ArgumentNullException.ThrowIfNull(deleteDirectory);
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FolderSessionLock.Tests");
        string path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("D"));

        Directory.CreateDirectory(path);
        return new TemporaryTestDirectory(path, deleteDirectory);
    }

    public void VerifyAccessAndDeletion()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string probeDirectory = System.IO.Path.Combine(
            Path,
            $"access-probe-{Guid.NewGuid():D}");
        string probeFile = System.IO.Path.Combine(probeDirectory, "probe.txt");
        Directory.CreateDirectory(probeDirectory);
        File.WriteAllText(probeFile, "created");
        File.WriteAllText(probeFile, "written");
        if (File.ReadAllText(probeFile) != "written"
            || !Directory.EnumerateFileSystemEntries(probeDirectory).Contains(probeFile))
        {
            throw new IOException("The Folder Session Lock test directory access probe failed.");
        }

        File.Delete(probeFile);
        Directory.Delete(probeDirectory);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (Directory.Exists(Path))
            {
                _deleteDirectory(Path);
            }

            _disposed = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var failure = new IOException(
                $"Failed to clean up Folder Session Lock test directory '{Path}'.",
                exception);
            AclTestSafetyGate.Block(failure);
            throw failure;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
