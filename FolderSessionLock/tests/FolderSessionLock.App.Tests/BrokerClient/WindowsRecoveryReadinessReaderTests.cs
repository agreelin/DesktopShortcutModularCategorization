using FolderSessionLock.App.BrokerClient;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.App.Tests.BrokerClient;

public sealed class WindowsRecoveryReadinessReaderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task ReadAsync_ReturnsTheStrictCoreSnapshot()
    {
        RecoveryReadinessSnapshot expected = Ready();
        var reader = new WindowsRecoveryReadinessReader(new SnapshotPlatform(
            Result<byte[]>.Success(RecoveryReadinessJson.Serialize(expected))));

        RecoveryReadinessSnapshot actual = await reader.ReadAsync(default);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadAsync_PropagatesPlatformAndSchemaFailuresAsReadinessExceptions()
    {
        var platformFailure = new WindowsRecoveryReadinessReader(new SnapshotPlatform(
            Result<byte[]>.Failure(new Error(
                BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
                BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID,
                ErrorCategory.UnrecoverableError))));
        var schemaFailure = new WindowsRecoveryReadinessReader(new SnapshotPlatform(
            Result<byte[]>.Success("{}"u8.ToArray())));

        RecoveryReadinessException platform = await Assert.ThrowsAsync<RecoveryReadinessException>(
            () => platformFailure.ReadAsync(default).AsTask());
        RecoveryReadinessException schema = await Assert.ThrowsAsync<RecoveryReadinessException>(
            () => schemaFailure.ReadAsync(default).AsTask());

        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SECURITY_INVALID, platform.Code);
        Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_READINESS_SCHEMA_INVALID, schema.Code);
    }

    private static RecoveryReadinessSnapshot Ready() => new(
        RecoveryReadinessPolicy.SchemaVersion,
        RecoveryReadinessPolicy.ServiceName,
        Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"),
        1,
        RecoveryReadinessState.Ready,
        false,
        Now.AddSeconds(-2),
        Now.AddSeconds(-1),
        Now.AddSeconds(-1),
        Now.AddSeconds(29),
        0,
        null);

    private sealed class SnapshotPlatform(Result<byte[]> result)
        : IRecoveryReadinessSnapshotPlatform
    {
        public Result<byte[]> Read() => result;
    }
}
