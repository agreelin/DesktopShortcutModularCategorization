using System.Reflection;

namespace FolderSessionLock.Windows.Tests.Infrastructure;

public sealed class TemporaryTestDirectoryTests
{
    [Fact]
    public void Create_UsesRequiredRootAndGuidLeaf()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string expectedRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FolderSessionLock.Tests"));
        string actualParent = Directory.GetParent(directory.Path)!.FullName;
        string leaf = System.IO.Path.GetFileName(directory.Path);

        Assert.Equal(expectedRoot, actualParent, ignoreCase: true);
        Assert.True(Guid.TryParseExact(leaf, "D", out _));
        Assert.True(Directory.Exists(directory.Path));
    }

    [Fact]
    public void Dispose_DeletesDirectoryAndContents()
    {
        TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string path = directory.Path;
        File.WriteAllText(System.IO.Path.Combine(path, "probe.txt"), "probe");

        directory.Dispose();

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task DisposeAsync_DeletesDirectory()
    {
        string path;
        await using (TemporaryTestDirectory directory = TemporaryTestDirectory.Create())
        {
            path = directory.Path;
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void VerifyAccessAndDeletion_UsesOnlyTheOwnedTemporaryRoot()
    {
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();

        directory.VerifyAccessAndDeletion();

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void Dispose_DeleteFailureBlocksFurtherAclWritesAndRestoresTestGate()
    {
        AclTestSafetyGate.EnsureCanWrite();
        Exception? previousFailure = AclTestSafetyGate.CaptureFailure();
        TemporaryTestDirectory? directory = null;
        string? path = null;
        try
        {
            directory = TemporaryTestDirectory.Create(_ =>
                throw new IOException("Injected delete failure."));
            path = directory.Path;

            IOException failure = Assert.Throws<IOException>(directory.Dispose);

            Assert.Contains(path, failure.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(AclTestSafetyGate.EnsureCanWrite);
        }
        finally
        {
            if (path is not null && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            AclTestSafetyGate.RestoreFailure(previousFailure);
        }

        AclTestSafetyGate.EnsureCanWrite();
        Assert.NotNull(directory);
        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public void PublicApi_DoesNotAcceptAnExternalPath()
    {
        ConstructorInfo[] constructors = typeof(TemporaryTestDirectory).GetConstructors();
        MethodInfo[] publicStaticMethods = typeof(TemporaryTestDirectory).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.Empty(constructors);
        Assert.DoesNotContain(
            publicStaticMethods,
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string)));
    }
}
