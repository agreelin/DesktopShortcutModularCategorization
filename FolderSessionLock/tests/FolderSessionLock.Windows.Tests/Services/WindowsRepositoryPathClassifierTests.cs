using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Services;
using FolderSessionLock.Windows.Tests.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Tests.Services;

public sealed class WindowsRepositoryPathClassifierTests
{
    [Theory]
    [InlineData(".git", true)]
    [InlineData(".git", false)]
    [InlineData(".hg", true)]
    [InlineData(".svn", true)]
    public void Classifier_FindsClosedMarkerSetOnTargetOrAncestor(
        string marker,
        bool markerIsDirectory)
    {
        using TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        string repository = Path.Combine(temporary.Path, "Repository");
        string target = Path.Combine(repository, "Child", "Target");
        Directory.CreateDirectory(target);
        string markerPath = Path.Combine(repository, marker);
        if (markerIsDirectory)
        {
            Directory.CreateDirectory(markerPath);
        }
        else
        {
            File.WriteAllBytes(markerPath, []);
        }

        using ValidatedDirectory directory = Validate(temporary.Path, target);
        var platform = new WindowsRepositoryPathPlatform();
        var classifier = new WindowsRepositoryPathClassifier(platform);

        Result<bool> result = classifier.IsUnderRepositoryRoot(directory);

        Assert.True(result.IsSuccess, $"{result.Error?.Code}; status=0x{platform.LastStatus:x8}");
        Assert.True(result.Value);
    }

    [Fact]
    public void Classifier_ReachesVolumeRootWithoutUsingEnvironmentRoots()
    {
        using TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        string target = Path.Combine(temporary.Path, "Plain", "Child");
        Directory.CreateDirectory(target);
        using ValidatedDirectory directory = Validate(temporary.Path, target);

        var platform = new WindowsRepositoryPathPlatform();
        Result<bool> result = new WindowsRepositoryPathClassifier(platform)
            .IsUnderRepositoryRoot(directory);

        Assert.True(result.IsSuccess, $"{result.Error?.Code}; status=0x{platform.LastStatus:x8}");
        Assert.False(result.Value);
    }

    [Fact]
    public void MarkerProbeFailure_FailsClosed()
    {
        using TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        string target = Path.Combine(temporary.Path, "Plain", "Child");
        Directory.CreateDirectory(target);
        using ValidatedDirectory directory = Validate(temporary.Path, target);
        var platform = new FailingMarkerPlatform();

        Result<bool> result = new WindowsRepositoryPathClassifier(platform)
            .IsUnderRepositoryRoot(directory);

        Assert.True(result.IsFailure);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE,
            result.Error!.Code);
        Assert.Equal([".git"], platform.ProbedMarkers);
    }

    [Fact]
    public void AncestorOpenFailure_FailsClosed()
    {
        using TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        string target = Path.Combine(temporary.Path, "Plain", "Child");
        Directory.CreateDirectory(target);
        using ValidatedDirectory directory = Validate(temporary.Path, target);

        Result<bool> result = new WindowsRepositoryPathClassifier(
            new FailingAncestorPlatform())
            .IsUnderRepositoryRoot(directory);

        Assert.True(result.IsFailure);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE,
            result.Error!.Code);
    }

    [Fact]
    public void AncestorTraversal_UsesRetainedRootRelativeHandlesAndChildIdentityBindings()
    {
        using TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        string target = Path.Combine(temporary.Path, "Plain", "Child", "Target");
        Directory.CreateDirectory(target);
        using ValidatedDirectory directory = Validate(temporary.Path, target);
        var platform = new RecordingChainPlatform();

        Result<bool> result = new WindowsRepositoryPathClassifier(platform)
            .IsUnderRepositoryRoot(directory);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value);
        Assert.True(platform.RelativeAncestorOpenCount > 0);
        Assert.True(platform.ChildBindingCount > 0);
        Assert.Contains("Target", platform.ChildLeaves, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AncestorChildReplacementRace_FailsClosedOnIdentityMismatch()
    {
        using TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
        string target = Path.Combine(temporary.Path, "Plain", "Child");
        Directory.CreateDirectory(target);
        using ValidatedDirectory directory = Validate(temporary.Path, target);

        Result<bool> result = new WindowsRepositoryPathClassifier(
            new ReplacedChildPlatform())
            .IsUnderRepositoryRoot(directory);

        Assert.True(result.IsFailure);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE,
            result.Error!.Code);
    }

    private static ValidatedDirectory Validate(string root, string target)
    {
        Result<ValidatedDirectory> result = new WindowsFolderPathValidator(CreatePolicy(root))
            .Validate(target);
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value;
    }

    private static FolderPathSafetyPolicy CreatePolicy(string root)
    {
        string userProfile = Path.Combine(root, "PolicyUserProfile");
        return new FolderPathSafetyPolicy(
            Path.Combine(root, "PolicyRepository"),
            Path.Combine(root, "PolicyInstallation"),
            [Path.Combine(root, "PolicySynchronization")],
            new SystemPathRoots(
                userProfile,
                Path.Combine(userProfile, "Desktop"),
                Path.Combine(userProfile, "Documents"),
                Path.Combine(userProfile, "Downloads"),
                Path.Combine(root, "PolicyWindows"),
                Path.Combine(root, "PolicyWindows", "System"),
                [Path.Combine(root, "PolicyProgramFiles")],
                Path.Combine(root, "PolicyProgramData")));
    }

    private sealed class FailingMarkerPlatform : WindowsRepositoryPathPlatform
    {
        internal List<string> ProbedMarkers { get; } = [];

        internal override RepositoryMarkerProbe ProbeMarker(
            SafeFileHandle directoryHandle,
            string marker)
        {
            ProbedMarkers.Add(marker);
            return RepositoryMarkerProbe.Error;
        }
    }

    private sealed class FailingAncestorPlatform : WindowsRepositoryPathPlatform
    {
        internal override RepositoryMarkerProbe ProbeMarker(
            SafeFileHandle directoryHandle,
            string marker) => RepositoryMarkerProbe.NotFound;

        internal override Result<SafeFileHandle> OpenAncestor(
            SafeFileHandle volumeRoot,
            string relativePath) => Result<SafeFileHandle>.Failure(new Error(
                BrokerErrorCodes.FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE,
                BrokerErrorCodes.FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE,
                ErrorCategory.UnrecoverableError));
    }

    private sealed class RecordingChainPlatform : WindowsRepositoryPathPlatform
    {
        internal int RelativeAncestorOpenCount { get; private set; }
        internal int ChildBindingCount { get; private set; }
        internal List<string> ChildLeaves { get; } = [];

        internal override Result<SafeFileHandle> OpenAncestor(
            SafeFileHandle volumeRoot,
            string relativePath)
        {
            RelativeAncestorOpenCount++;
            return base.OpenAncestor(volumeRoot, relativePath);
        }

        internal override Result<DirectoryIdentity> GetChildIdentity(
            SafeFileHandle parent,
            string childLeaf)
        {
            ChildBindingCount++;
            ChildLeaves.Add(childLeaf);
            return base.GetChildIdentity(parent, childLeaf);
        }
    }

    private sealed class ReplacedChildPlatform : WindowsRepositoryPathPlatform
    {
        internal override Result<DirectoryIdentity> GetChildIdentity(
            SafeFileHandle parent,
            string childLeaf) => Result<DirectoryIdentity>.Success(new(
                ulong.MaxValue,
                ulong.MaxValue,
                ulong.MaxValue));
    }
}
