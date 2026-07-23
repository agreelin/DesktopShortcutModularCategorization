using FolderSessionLock.Broker.Recovery;

namespace FolderSessionLock.App.Tests.BrokerRecovery;

public sealed class WindowsRecoveryServiceHostTests
{
    [Theory]
    [InlineData((int)RecoveryServiceState.StartPending, WindowsRecoveryServiceStatusReporter.ServiceStartPending)]
    [InlineData((int)RecoveryServiceState.Preflight, WindowsRecoveryServiceStatusReporter.ServiceStartPending)]
    [InlineData((int)RecoveryServiceState.Scanning, WindowsRecoveryServiceStatusReporter.ServiceStartPending)]
    [InlineData((int)RecoveryServiceState.Ready, WindowsRecoveryServiceStatusReporter.ServiceRunning)]
    [InlineData((int)RecoveryServiceState.RecoveryBlocked, WindowsRecoveryServiceStatusReporter.ServiceRunning)]
    [InlineData((int)RecoveryServiceState.Stopping, WindowsRecoveryServiceStatusReporter.ServiceStopPending)]
    [InlineData((int)RecoveryServiceState.Stopped, WindowsRecoveryServiceStatusReporter.ServiceStopped)]
    public void StatusReporter_MapsTheD024ServiceStateMachineExactly(
        int stateValue,
        uint expected)
    {
        var state = (RecoveryServiceState)stateValue;
        Assert.Equal(expected, WindowsRecoveryServiceStatusReporter.MapState(state));
    }

    [Fact]
    public void Host_RunsOnlyThroughTheConfiguredServiceDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var host = new WindowsRecoveryServiceHost(
            dispatcher,
            (reporter, cancellationToken) => Task.FromResult(37));

        int result = host.Run();

        Assert.Equal(37, result);
        Assert.Equal(1, dispatcher.RunCount);
    }

    private sealed class RecordingDispatcher : IRecoveryServiceDispatcher
    {
        internal int RunCount { get; private set; }

        public int Run(
            Func<IRecoveryServiceStatusReporter, CancellationToken, Task<int>> serviceMain)
        {
            RunCount++;
            return serviceMain(new RecordingReporter(), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
    }

    private sealed class RecordingReporter : IRecoveryServiceStatusReporter
    {
        public ValueTask ReportAsync(
            RecoveryServiceStatusSnapshot snapshot,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
