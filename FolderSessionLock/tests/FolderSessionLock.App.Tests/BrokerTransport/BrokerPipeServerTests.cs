using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Broker.Transport;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Services;

namespace FolderSessionLock.Broker.Transport.Tests;

public sealed class BrokerPipeServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 16, 30, 0, TimeSpan.Zero);
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromMinutes(1), TimeSpan.FromHours(8)).Value;

    [Fact]
    public void Endpoint_UsesOnlyTheFixedNameAndLocalPath()
    {
        Assert.Equal("FolderSessionLock.Broker.v1", BrokerPipeEndpoint.PipeName);
        Assert.Equal(@"\\.\pipe\FolderSessionLock.Broker.v1", BrokerPipeEndpoint.LocalPath);
        Assert.Equal(TimeSpan.FromSeconds(15), BrokerPipeServer.ClientConnectTimeout);
        BrokerPipeEndpoint.EnsureFixedName("FolderSessionLock.Broker.v1");
        Assert.Throws<ArgumentException>(() => BrokerPipeEndpoint.EnsureFixedName("foldersessionlock.broker.v1"));
        Assert.Throws<ArgumentException>(() => BrokerPipeEndpoint.EnsureFixedName("FolderSessionLock.Broker.v2"));
    }

    [Fact]
    public async Task RunOnceAsync_ClientConnectTimeoutReturnsTheFixedPreConnectError()
    {
        (SecurityIdentifier logonSid, SecurityIdentifier brokerSid) = await CurrentSids();
        string replayRoot = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        try
        {
            var replay = new FileReplayRegistry(
                replayRoot,
                $"Local\\FolderSessionLock.Tests.{Guid.NewGuid():N}",
                new FixedClock(Now),
                new NoneEvidence());

            BrokerPipeConnectionResult result = await BrokerPipeServer.RunOnceAsync(
                logonSid,
                brokerSid,
                new BrokerConsentOptions(
                    BrokerPipeEndpoint.PipeName,
                    1,
                    Guid.NewGuid(),
                    1234,
                    133970112000000000),
                DurationPolicy,
                new FixedClock(Now),
                new UnusedAuthenticator(),
                replay,
                (_, _) => throw new InvalidOperationException(),
                clientConnectTimeout: TimeSpan.FromMilliseconds(20));

            Assert.False(result.ResponseWritten);
            Assert.Equal(BrokerErrorCodes.FSL_E_BROKER_CONNECT_TIMEOUT, result.Error!.Code);
            Assert.Equal(
                "The elevated broker did not establish a secure connection in time.",
                result.Error.Message);
            Assert.True(result.Error.Retryable);
            Assert.Null(result.Error.Field);
        }
        finally
        {
            if (Directory.Exists(replayRoot))
            {
                Directory.Delete(replayRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Create_UsesByteModeAndDaclWithOnlyExpectedSubjects()
    {
        (SecurityIdentifier logonSid, SecurityIdentifier brokerSid) = await CurrentSids();
        using NamedPipeServerStream pipe = BrokerPipeServer.Create(logonSid, brokerSid);
        PipeSecurity security = pipe.GetAccessControl();
        PipeAccessRule[] accessRules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        Assert.Equal(PipeTransmissionMode.Byte, pipe.TransmissionMode);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(2, accessRules.Length);
        Assert.All(accessRules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, rule.PipeAccessRights);
        });
        Assert.Equal(
            new[] { brokerSid.Value, logonSid.Value }.Order(StringComparer.Ordinal),
            accessRules.Select(rule => rule.IdentityReference.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RunOnceAsync_CancellationWhileWaitingReturnsOperationCancelled()
    {
        (SecurityIdentifier logonSid, SecurityIdentifier brokerSid) = await CurrentSids();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        string replayRoot = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        try
        {
            var replay = new FileReplayRegistry(
                replayRoot,
                $"Local\\FolderSessionLock.Tests.{Guid.NewGuid():N}",
                new FixedClock(Now),
                new NoneEvidence());

            BrokerPipeConnectionResult result = await BrokerPipeServer.RunOnceAsync(
                logonSid,
                brokerSid,
                new BrokerConsentOptions(
                    BrokerPipeEndpoint.PipeName,
                    1,
                    Guid.NewGuid(),
                    1234,
                    133970112000000000),
                DurationPolicy,
                new FixedClock(Now),
                new UnusedAuthenticator(),
                replay,
                (_, _) => throw new InvalidOperationException(),
                cancellation.Token);

            Assert.False(result.ResponseWritten);
            Assert.Equal(BrokerErrorCodes.FSL_E_OPERATION_CANCELLED, result.Error!.Code);
        }
        finally
        {
            if (Directory.Exists(replayRoot))
            {
                Directory.Delete(replayRoot, recursive: true);
            }
        }
    }

    private static async Task<(SecurityIdentifier LogonSid, SecurityIdentifier BrokerSid)> CurrentSids()
    {
        var identity = await new WindowsSessionIdentityProvider().GetCurrentAsync();
        Assert.True(identity.IsSuccess, identity.Error?.Message);
        return (
            new SecurityIdentifier(identity.Value.LogonSid),
            new SecurityIdentifier(identity.Value.AccountSid));
    }

    private sealed class UnusedAuthenticator : IBrokerConnectionAuthenticator
    {
        public ValueTask<BrokerAuthenticationResult> AuthenticateAsync(
            Stream stream,
            BrokerClientHello hello,
            BrokerConsentOptions options,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            new(Task.Delay(delay, cancellationToken));
    }

    private sealed class NoneEvidence : IReplaySideEffectEvidenceProvider
    {
        public ReplaySideEffectEvidence Inspect(Guid requestId) => ReplaySideEffectEvidence.None;
    }
}
