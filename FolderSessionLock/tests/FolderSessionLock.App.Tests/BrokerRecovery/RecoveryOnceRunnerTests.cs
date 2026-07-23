using System.Text.Json;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class RecoveryOnceRunnerTests
{
    [Fact]
    public async Task RunAsync_UsesOnlyTheEightDocumentedExitCodesAndPriority()
    {
        Assert.Equal(
            RecoveryOnceExitCode.InvalidArguments,
            await Runner(Summary(false, null)).RunAsync(argumentsValid: false));
        Assert.Equal(
            RecoveryOnceExitCode.ProtectedStorageSecurityFailure,
            await Runner(Summary(true, BrokerErrorCodes.FSL_E_PROTECTED_PATH_DACL_MISMATCH))
                .RunAsync(argumentsValid: true));
        Assert.Equal(
            RecoveryOnceExitCode.RecoveryEnumerationFailure,
            await Runner(Summary(true, BrokerErrorCodes.FSL_E_RECOVERY_DIRECTORY_ENUMERATION_FAILED))
                .RunAsync(argumentsValid: true));
        Assert.Equal(
            RecoveryOnceExitCode.RecoveryRecordLimitExceeded,
            await Runner(Summary(true, BrokerErrorCodes.FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED))
                .RunAsync(argumentsValid: true));
        Assert.Equal(
            RecoveryOnceExitCode.RecoveryBlocked,
            await Runner(Summary(true, "FSL_E_RECORD_FAILURE", failed: 1))
                .RunAsync(argumentsValid: true));
        Assert.Equal(
            RecoveryOnceExitCode.Cancelled,
            await Runner(Summary(true, BrokerErrorCodes.FSL_E_OPERATION_CANCELLED, skipped: 1))
                .RunAsync(argumentsValid: true));
        Assert.Equal(
            RecoveryOnceExitCode.Success,
            await Runner(Summary(false, null)).RunAsync(argumentsValid: true));
        Assert.Equal(
            RecoveryOnceExitCode.InternalFailure,
            await new RecoveryOnceRunner(_ => throw new InvalidOperationException())
                .RunAsync(argumentsValid: true));

        Assert.Equal([0, 2, 10, 11, 12, 13, 14, 15], Enum.GetValues<RecoveryOnceExitCode>().Select(value => (int)value));

        RecoveryOnceRunner serialized = Runner(Summary(false, null));
        Assert.Equal(RecoveryOnceExitCode.Success, await serialized.RunAsync(argumentsValid: true));
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(serialized.LastSummary));
        Assert.Equal(
            [
                "canonicalRecordCount",
                "processedRecordCount",
                "cleanedCount",
                "alreadyCleanCount",
                "failedCount",
                "recoveryRequiredCount",
                "skippedCount",
                "auxiliaryArtifactCount",
                "invalidArtifactCount",
                "remainingRecordCount",
                "recoveryBlocking",
                "primaryErrorCode",
            ],
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    private static RecoveryOnceRunner Runner(RecoveryRunSummary summary) => new(
        _ => ValueTask.FromResult(summary));

    private static RecoveryRunSummary Summary(
        bool blocking,
        string? error,
        int failed = 0,
        int skipped = 0) => new(
            failed + skipped,
            failed,
            0,
            0,
            failed,
            0,
            skipped,
            0,
            0,
            failed + skipped,
            blocking,
            error);
}
