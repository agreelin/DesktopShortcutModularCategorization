using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Services;

namespace FolderSessionLock.Windows.Tests.Services;

public sealed class WindowsSessionIdentityProviderTests
{
    private const string AccountSid = "S-1-5-21-100-200-300-400";
    private const string LogonSid = "S-1-5-5-100-200";
    private const string OtherLogonSid = "S-1-5-5-300-400";

    [Fact]
    public async Task GetCurrentAsync_ReturnsCurrentTokenIdentity()
    {
        var provider = new WindowsSessionIdentityProvider();

        Result<SessionIdentity> result = await provider.GetCurrentAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.AccountSid));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.LogonSid));
        Assert.True(result.Value.WindowsSessionId >= 0);
        Assert.NotEqual(result.Value.AccountSid, result.Value.LogonSid);
    }

    [Fact]
    public void SelectUniqueLogonSid_NoMatchFailsWithoutAccountSidFallback()
    {
        WindowsSessionIdentityProvider.TokenGroupIdentity[] groups =
        [
            new(AccountSid, 0),
            new(LogonSid, 0x80000000),
            new(OtherLogonSid, 0x40000000),
        ];

        Result<string> result = WindowsSessionIdentityProvider.SelectUniqueLogonSid(groups);

        Assert.True(result.IsFailure);
        Assert.Equal("windows.session_identity.logon_sid_not_found", result.Error!.Code);
    }

    [Fact]
    public void SelectUniqueLogonSid_OneFullMatchReturnsExactSid()
    {
        WindowsSessionIdentityProvider.TokenGroupIdentity[] groups =
        [
            new(AccountSid, 0),
            new(LogonSid, NativeMethods.SeGroupLogonId | 0x00000004),
        ];

        Result<string> result = WindowsSessionIdentityProvider.SelectUniqueLogonSid(groups);

        Assert.True(result.IsSuccess);
        Assert.Equal(LogonSid, result.Value);
    }

    [Fact]
    public void SelectUniqueLogonSid_TwoFullMatchesFail()
    {
        WindowsSessionIdentityProvider.TokenGroupIdentity[] groups =
        [
            new(LogonSid, NativeMethods.SeGroupLogonId),
            new(OtherLogonSid, NativeMethods.SeGroupLogonId | 0x00000004),
        ];

        Result<string> result = WindowsSessionIdentityProvider.SelectUniqueLogonSid(groups);

        Assert.True(result.IsFailure);
        Assert.Equal("windows.session_identity.logon_sid_not_unique", result.Error!.Code);
    }

    [Fact]
    public async Task GetCurrentAsync_PreCanceledTokenThrowsBeforeReadingIdentity()
    {
        var provider = new WindowsSessionIdentityProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetCurrentAsync(cancellation.Token).AsTask());
    }
}
