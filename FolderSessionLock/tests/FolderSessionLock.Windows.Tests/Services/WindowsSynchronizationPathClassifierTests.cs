using System.Runtime.InteropServices;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Services;
using FolderSessionLock.Windows.Tests.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Tests.Services;

public sealed class WindowsSynchronizationPathClassifierTests
{
    [Fact]
    public void StatusCloudFileNotUnderSyncRoot_UsesTheApprovedRawNtStatus()
    {
        Assert.Equal(
            unchecked((int)0xC000CF13),
            WindowsSynchronizationPathPlatform.StatusCloudFileNotUnderSyncRoot);
        Assert.Equal(
            -1073688813,
            WindowsSynchronizationPathPlatform.StatusCloudFileNotUnderSyncRoot);
    }

    [Fact]
    public void HResultFromNtCloudFileNotUnderSyncRoot_UsesTheApprovedConversion()
    {
        int converted = unchecked(
            WindowsSynchronizationPathPlatform.StatusCloudFileNotUnderSyncRoot
            | (int)0x10000000);

        Assert.Equal(unchecked((int)0xD000CF13), converted);
        Assert.Equal(-805253357, converted);
        Assert.Equal(
            WindowsSynchronizationPathPlatform.HResultFromNtCloudFileNotUnderSyncRoot,
            converted);
    }

    [Fact]
    public void CloudFilesNotUnderRoot_RecognizesOnlyTheTwoApprovedHResults()
    {
        Assert.True(WindowsSynchronizationPathPlatform.IsNotUnderSyncRoot(
            WindowsSynchronizationPathPlatform.HResultFromWin32CloudFileNotUnderSyncRoot));
        Assert.True(WindowsSynchronizationPathPlatform.IsNotUnderSyncRoot(
            WindowsSynchronizationPathPlatform.HResultFromNtCloudFileNotUnderSyncRoot));
        Assert.False(WindowsSynchronizationPathPlatform.IsNotUnderSyncRoot(
            WindowsSynchronizationPathPlatform.StatusCloudFileNotUnderSyncRoot));
        Assert.False(WindowsSynchronizationPathPlatform.IsNotUnderSyncRoot(
            unchecked((int)0x80004005)));
    }

    [Fact]
    public void CloudFilesSuccess_RejectsWithoutConsultingSkyDrive()
    {
        using Context context = Context.Create();
        var platform = new FakePlatform(Result<bool>.Success(true), FailureLookup());

        Result<bool> result = new WindowsSynchronizationPathClassifier(platform)
            .IsUnderSynchronizationRoot(context.Directory);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.Equal(0, platform.LookupCount);
    }

    [Fact]
    public void ExplicitNotUnderCloudRoot_UsesInitiatingUserSkyDriveRelationship()
    {
        using Context context = Context.Create();
        var platform = new FakePlatform(
            Result<bool>.Success(false),
            Result<KnownFolderLookup>.Success(new(true, context.SkyDriveRoot)));

        Result<bool> result = new WindowsSynchronizationPathClassifier(platform)
            .IsUnderSynchronizationRoot(context.Directory);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.True(result.Value);
        Assert.Equal(1, platform.LookupCount);
    }

    [Fact]
    public void MissingSkyDrive_AllowsOnlyAfterCloudFilesExplicitlySaysNotUnderRoot()
    {
        using Context context = Context.Create(targetUnderSkyDrive: false);
        var platform = new FakePlatform(
            Result<bool>.Success(false),
            Result<KnownFolderLookup>.Success(new(false, null)));

        Result<bool> result = new WindowsSynchronizationPathClassifier(platform)
            .IsUnderSynchronizationRoot(context.Directory);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
    }

    [Fact]
    public void IndeterminateCloudOrKnownFolderFailure_FailsClosed()
    {
        using Context context = Context.Create();
        var cloudFailure = new WindowsSynchronizationPathClassifier(new FakePlatform(
            FailureBool(),
            Result<KnownFolderLookup>.Success(new(false, null))));
        var knownFolderFailure = new WindowsSynchronizationPathClassifier(new FakePlatform(
            Result<bool>.Success(false),
            FailureLookup()));

        Assert.Equal(
            BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
            cloudFailure.IsUnderSynchronizationRoot(context.Directory).Error!.Code);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
            knownFolderFailure.IsUnderSynchronizationRoot(context.Directory).Error!.Code);
    }

    [Fact]
    public void SkyDriveKnownFolder_UsesApprovedFullNotFoundHResults()
    {
        Assert.Equal(
            unchecked((int)0x80070002),
            WindowsSynchronizationPathPlatform.HResultFileNotFound);
        Assert.Equal(
            -2147024894,
            WindowsSynchronizationPathPlatform.HResultFileNotFound);
        Assert.Equal(
            unchecked((int)0x80070003),
            WindowsSynchronizationPathPlatform.HResultPathNotFound);
        Assert.Equal(
            -2147024893,
            WindowsSynchronizationPathPlatform.HResultPathNotFound);
    }

    [Fact]
    public void SkyDriveKnownFolder_GetFolderIdsSOkWithoutSkyDrive_AllowsContinueAsNotRegistered()
    {
        using var platform = new NativeLookupPlatform(
            Result<Guid[]>.Success([]),
            hresult: 0,
            nativePath: @"C:\OneDrive");

        Result<KnownFolderLookup> result = platform.GetInitiatingUserSkyDrivePath();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Exists);
        Assert.Null(result.Value.Path);
        Assert.Equal("KnownFolderNotRegistered", result.Value.Reason);
        Assert.Equal(["GetFolderIds"], platform.CallOrder);
        Assert.Equal(0, platform.KnownFolderPathCallCount);
    }

    [Fact]
    public void SkyDriveKnownFolder_GetFolderIdsFailure_FailsClosedBeforePathLookup()
    {
        using var platform = new NativeLookupPlatform(
            FailureIds(),
            hresult: 0,
            nativePath: @"C:\OneDrive");

        Result<KnownFolderLookup> result = platform.GetInitiatingUserSkyDrivePath();

        Assert.True(result.IsFailure);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
            result.Error!.Code);
        Assert.Equal(["GetFolderIds"], platform.CallOrder);
        Assert.Equal(0, platform.KnownFolderPathCallCount);
    }

    [Fact]
    public void SkyDriveKnownFolder_RegisteredIdCallsExactDefaultLookupAndReturnsValidPath()
    {
        using var platform = RegisteredPlatform(hresult: 0, nativePath: @"C:\OneDrive");

        Result<KnownFolderLookup> result = platform.GetInitiatingUserSkyDrivePath();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Exists);
        Assert.Equal(@"C:\OneDrive", result.Value.Path);
        Assert.Null(result.Value.Reason);
        Assert.Equal(["GetFolderIds", "SHGetKnownFolderPath"], platform.CallOrder);
        Assert.Equal(WindowsKnownFolderPath.SkyDrive, platform.LastFolderId);
        Assert.Equal(WindowsSynchronizationPathPlatform.KnownFolderFlagsDefault, platform.LastFlags);
        Assert.Equal(0U, platform.LastFlags);
        Assert.True(platform.PathPointerWasNullBeforeCall);
        Assert.Equal(1, platform.FreeCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", true)]
    public void SkyDriveKnownFolder_SOkNullOrEmptyPath_FailsClosedAndFreesPointer(
        string? nativePath,
        bool allocatePointer)
    {
        using var platform = RegisteredPlatform(
            hresult: 0,
            nativePath,
            allocatePointer);

        Result<KnownFolderLookup> result = platform.GetInitiatingUserSkyDrivePath();

        Assert.True(result.IsFailure);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
            result.Error!.Code);
        Assert.Equal(allocatePointer ? 1 : 0, platform.FreeCount);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002))]
    [InlineData(unchecked((int)0x80070003))]
    public void SkyDriveKnownFolder_ApprovedFullNotFoundHResults_AllowContinueAndFreePointer(
        int hresult)
    {
        using var platform = RegisteredPlatform(
            hresult,
            nativePath: "unexpected native path",
            allocatePointer: true);

        Result<KnownFolderLookup> result = platform.GetInitiatingUserSkyDrivePath();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Exists);
        Assert.Null(result.Value.Path);
        Assert.Equal(1, platform.FreeCount);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070057))]
    [InlineData(unchecked((int)0x80004005))]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x80070006))]
    [InlineData(unchecked((int)0x8007052E))]
    [InlineData(unchecked((int)0x80070520))]
    [InlineData(unchecked((int)0x80070522))]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(unchecked((int)0x81230002))]
    [InlineData(unchecked((int)0x81230003))]
    [InlineData(unchecked((int)0x8000FFFF))]
    public void SkyDriveKnownFolder_AllOtherResultsFailClosedAndFreeNonNullPointer(int hresult)
    {
        using var platform = RegisteredPlatform(
            hresult,
            nativePath: "unexpected native path",
            allocatePointer: true);

        Result<KnownFolderLookup> result = platform.GetInitiatingUserSkyDrivePath();

        Assert.True(result.IsFailure);
        Assert.Equal(
            BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
            result.Error!.Code);
        Assert.Equal(1, platform.FreeCount);
    }

    [Fact]
    public void SkyDriveKnownFolder_ProductSourceDoesNotUseForbiddenKnownFolderFlagsOrMasks()
    {
        string source = File.ReadAllText(Path.Combine(
            FindSolutionRoot(),
            "src",
            "FolderSessionLock.Windows",
            "Services",
            "WindowsSynchronizationPathClassifier.cs"));

        Assert.Contains("KnownFolderFlagsDefault = 0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KF_FLAG_CREATE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KF_FLAG_DONT_VERIFY", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KF_FLAG_DEFAULT_PATH", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HRESULT_CODE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("& 0xFFFF", source, StringComparison.OrdinalIgnoreCase);
    }

    private static Result<bool> FailureBool() => Result<bool>.Failure(new Error(
        BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
        BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
        ErrorCategory.UnrecoverableError));

    private static Result<KnownFolderLookup> FailureLookup() =>
        Result<KnownFolderLookup>.Failure(new Error(
            BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
            BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
            ErrorCategory.UnrecoverableError));

    private static Result<Guid[]> FailureIds() => Result<Guid[]>.Failure(new Error(
        BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
        BrokerErrorCodes.FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE,
        ErrorCategory.UnrecoverableError));

    private static NativeLookupPlatform RegisteredPlatform(
        int hresult,
        string? nativePath,
        bool allocatePointer = true) => new(
            Result<Guid[]>.Success([WindowsKnownFolderPath.SkyDrive]),
            hresult,
            nativePath,
            allocatePointer);

    private static string FindSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FolderSessionLock.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "FolderSessionLock.sln was not found above the test output directory.");
    }

    private sealed class FakePlatform(
        Result<bool> cloudResult,
        Result<KnownFolderLookup> lookupResult)
        : WindowsSynchronizationPathPlatform(new FailingTokenSource())
    {
        internal int LookupCount { get; private set; }

        internal override Result<bool> IsUnderCloudFilesSyncRoot(SafeFileHandle targetHandle) =>
            cloudResult;

        internal override Result<KnownFolderLookup> GetInitiatingUserSkyDrivePath()
        {
            LookupCount++;
            return lookupResult;
        }
    }

    private sealed class FailingTokenSource : IInitiatingUserTokenSource
    {
        public Result<SafeAccessTokenHandle> GetToken() =>
            Result<SafeAccessTokenHandle>.Failure(new Error(
                "test.token.unavailable",
                "Token unavailable.",
                ErrorCategory.PlatformError));
    }

    private sealed class NativeLookupPlatform : WindowsSynchronizationPathPlatform, IDisposable
    {
        private readonly Result<Guid[]> _registeredIds;
        private readonly int _hresult;
        private readonly string? _nativePath;
        private readonly bool _allocatePointer;
        private readonly StaticTokenSource _tokenSource;

        internal NativeLookupPlatform(
            Result<Guid[]> registeredIds,
            int hresult,
            string? nativePath,
            bool allocatePointer = true)
            : this(
                new StaticTokenSource(),
                registeredIds,
                hresult,
                nativePath,
                allocatePointer)
        {
        }

        private NativeLookupPlatform(
            StaticTokenSource tokenSource,
            Result<Guid[]> registeredIds,
            int hresult,
            string? nativePath,
            bool allocatePointer)
            : base(tokenSource)
        {
            _tokenSource = tokenSource;
            _registeredIds = registeredIds;
            _hresult = hresult;
            _nativePath = nativePath;
            _allocatePointer = allocatePointer;
        }

        internal List<string> CallOrder { get; } = [];
        internal int KnownFolderPathCallCount { get; private set; }
        internal Guid LastFolderId { get; private set; }
        internal uint LastFlags { get; private set; }
        internal bool PathPointerWasNullBeforeCall { get; private set; }
        internal int FreeCount { get; private set; }

        internal override Result<Guid[]> GetRegisteredKnownFolderIds()
        {
            CallOrder.Add("GetFolderIds");
            return _registeredIds;
        }

        internal override int GetKnownFolderPath(
            in Guid folderId,
            uint flags,
            nint token,
            ref nint pathPointer)
        {
            CallOrder.Add("SHGetKnownFolderPath");
            KnownFolderPathCallCount++;
            LastFolderId = folderId;
            LastFlags = flags;
            PathPointerWasNullBeforeCall = pathPointer == 0;
            if (_allocatePointer)
            {
                pathPointer = Marshal.StringToCoTaskMemUni(_nativePath ?? "unexpected native path");
            }

            return _hresult;
        }

        internal override void FreeKnownFolderPath(nint pathPointer)
        {
            FreeCount++;
            Marshal.FreeCoTaskMem(pathPointer);
        }

        public void Dispose() => _tokenSource.Dispose();
    }

    private sealed class StaticTokenSource : IInitiatingUserTokenSource, IDisposable
    {
        private readonly SafeAccessTokenHandle _token = new(new nint(1));

        public Result<SafeAccessTokenHandle> GetToken() =>
            Result<SafeAccessTokenHandle>.Success(_token);

        public void Dispose() => _token.Dispose();
    }

    private sealed class Context : IDisposable
    {
        private readonly TemporaryTestDirectory _temporary;

        private Context(
            TemporaryTestDirectory temporary,
            string skyDriveRoot,
            ValidatedDirectory directory)
        {
            _temporary = temporary;
            SkyDriveRoot = skyDriveRoot;
            Directory = directory;
        }

        internal string SkyDriveRoot { get; }
        internal ValidatedDirectory Directory { get; }

        internal static Context Create(bool targetUnderSkyDrive = true)
        {
            TemporaryTestDirectory temporary = TemporaryTestDirectory.Create();
            try
            {
                string skyDrive = Path.Combine(temporary.Path, "SkyDrive");
                string target = targetUnderSkyDrive
                    ? Path.Combine(skyDrive, "Child")
                    : Path.Combine(temporary.Path, "Plain", "Child");
                System.IO.Directory.CreateDirectory(target);
                Result<ValidatedDirectory> validated = new WindowsFolderPathValidator(
                    CreatePolicy(temporary.Path)).Validate(target);
                Assert.True(validated.IsSuccess, validated.Error?.Code);
                return new Context(temporary, skyDrive, validated.Value);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Directory.Dispose();
            _temporary.Dispose();
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
    }
}
