using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Services;

namespace FolderSessionLock.Windows.Tests.Services;

public sealed class SafePlaceholderTests
{
    [Fact]
    public async Task FolderLockService_FailsWithoutAccessingPath()
    {
        var service = new UnavailableFolderLockService();
        var request = new FolderLockRequest(
            Guid.NewGuid(),
            @"Z:\path-that-must-not-be-accessed",
            TimeSpan.FromMinutes(1));

        Result<Guid> result = await service.CreateLockAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.PlatformError, result.Error!.Category);
        Assert.Equal("windows.acl.not_implemented", result.Error.Code);
    }

    [Fact]
    public async Task AccessAttemptMonitor_RemainsDisabled()
    {
        var monitor = new DisabledAccessAttemptMonitor();

        Result result = await monitor.StartAsync(
            Guid.NewGuid(),
            @"Z:\path-that-must-not-be-accessed");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.RecoverableError, result.Error!.Category);
        Assert.Equal("windows.access_monitor.disabled", result.Error.Code);
    }

    [Theory]
    [InlineData(LockRemovalIntent.Expiration)]
    [InlineData(LockRemovalIntent.Recovery)]
    [InlineData(LockRemovalIntent.TestCleanup)]
    [InlineData(LockRemovalIntent.AdministrativeCleanup)]
    public async Task FolderLockService_RemoveFailsForEveryExplicitIntent(
        LockRemovalIntent intent)
    {
        var service = new UnavailableFolderLockService();

        Result result = await service.RemoveLockAsync(Guid.NewGuid(), intent);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.PlatformError, result.Error!.Category);
        Assert.Equal("windows.acl.not_implemented", result.Error.Code);
    }

    [Fact]
    public async Task SystemClock_PreCanceledDelayIsCanceledWithoutWaiting()
    {
        var clock = new SystemClock();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => clock.DelayAsync(TimeSpan.FromHours(1), cancellation.Token).AsTask());
    }
}
