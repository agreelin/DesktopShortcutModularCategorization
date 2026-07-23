using FolderSessionLock.Broker.Transport;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class RecoveryReadinessTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 7, 21, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Policy_AllowsOnlyTheSingleDocumentedReadyShape()
    {
        RecoveryReadinessSnapshot ready = Ready();

        Assert.True(RecoveryReadinessPolicy.IsReady(ready, Started.AddSeconds(2)));
        Assert.False(RecoveryReadinessPolicy.IsReady(null, Started.AddSeconds(2)));
        Assert.False(RecoveryReadinessPolicy.IsReady(ready with { SchemaVersion = 2 }, Started.AddSeconds(2)));
        Assert.False(RecoveryReadinessPolicy.IsReady(ready with { State = RecoveryReadinessState.Starting }, Started.AddSeconds(2)));
        Assert.False(RecoveryReadinessPolicy.IsReady(ready with { RecoveryBlocking = true }, Started.AddSeconds(2)));
        Assert.False(RecoveryReadinessPolicy.IsReady(ready with { ScanCompletedUtc = null }, Started.AddSeconds(2)));
        Assert.False(RecoveryReadinessPolicy.IsReady(ready with { RemainingRecordCount = 1 }, Started.AddSeconds(2)));
        Assert.False(RecoveryReadinessPolicy.IsReady(ready with { PrimaryErrorCode = "FSL_E_TEST" }, Started.AddSeconds(2)));
    }

    [Fact]
    public async Task Gate_FailsClosedForReaderFailureAndReturnsTheExactPublicError()
    {
        var gate = new RecoveryCreateLockGate(new ThrowingReader());

        BrokerError? error = await gate.CheckAsync(default);

        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_BLOCKING, error!.Code);
        Assert.Equal("Folder restrictions cannot be created until recovery is complete.", error.Message);
        Assert.True(error.Retryable);
        Assert.Null(error.Field);
    }

    internal static RecoveryCreateLockGate ReadyGate() => new(new FixedReader(
        Ready(DateTimeOffset.UtcNow)));

    internal static RecoveryCreateLockGate BlockedGate() => new(new FixedReader(
        Ready(DateTimeOffset.UtcNow) with
        {
            State = RecoveryReadinessState.RecoveryBlocked,
            RecoveryBlocking = true,
            PrimaryErrorCode = BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_INVALID,
        }));

    private static RecoveryReadinessSnapshot Ready() => Ready(Started.AddSeconds(1));

    private static RecoveryReadinessSnapshot Ready(DateTimeOffset publishedUtc) => new(
        1,
        "FolderSessionLockRecovery",
        Guid.Parse("11111111-2222-4333-8444-555555555555"),
        1,
        RecoveryReadinessState.Ready,
        false,
        publishedUtc.AddSeconds(-1),
        publishedUtc,
        publishedUtc,
        publishedUtc.AddSeconds(30),
        0,
        null);

    private sealed class FixedReader(RecoveryReadinessSnapshot snapshot) : IRecoveryReadinessReader
    {
        public ValueTask<RecoveryReadinessSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);
    }

    private sealed class ThrowingReader : IRecoveryReadinessReader
    {
        public ValueTask<RecoveryReadinessSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<RecoveryReadinessSnapshot>(new IOException("unavailable"));
    }
}
