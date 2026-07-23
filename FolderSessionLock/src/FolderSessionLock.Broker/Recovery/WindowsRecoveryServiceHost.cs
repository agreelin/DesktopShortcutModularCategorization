using System.ComponentModel;
using System.Runtime.InteropServices;
using FolderSessionLock.Core.Recovery;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Recovery;

internal interface IRecoveryServiceDispatcher
{
    int Run(Func<IRecoveryServiceStatusReporter, CancellationToken, Task<int>> serviceMain);
}

internal sealed class WindowsRecoveryServiceHost
{
    private readonly IRecoveryServiceDispatcher _dispatcher;
    private readonly Func<IRecoveryServiceStatusReporter, CancellationToken, Task<int>> _serviceMain;

    internal WindowsRecoveryServiceHost(
        IRecoveryServiceDispatcher dispatcher,
        Func<IRecoveryServiceStatusReporter, CancellationToken, Task<int>> serviceMain)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _serviceMain = serviceMain ?? throw new ArgumentNullException(nameof(serviceMain));
    }

    internal int Run() => _dispatcher.Run(_serviceMain);
}

internal sealed class WindowsRecoveryServiceDispatcher : IRecoveryServiceDispatcher
{
    private const uint ServiceControlStop = 1;
    private const uint ErrorCallNotImplemented = 120;
    private readonly CancellationTokenSource _stop = new();
    private ServiceMainCallback? _serviceMainCallback;
    private ServiceControlHandler? _controlHandler;
    private Func<IRecoveryServiceStatusReporter, CancellationToken, Task<int>>? _serviceMain;
    private int _exitCode = (int)RecoveryOnceExitCode.InternalFailure;

    public int Run(Func<IRecoveryServiceStatusReporter, CancellationToken, Task<int>> serviceMain)
    {
        _serviceMain = serviceMain ?? throw new ArgumentNullException(nameof(serviceMain));
        _serviceMainCallback = ServiceMain;
        _controlHandler = HandleControl;
        ServiceTableEntry[] table =
        [
            new(RecoveryReadinessPolicy.ServiceName, _serviceMainCallback),
            new(null, null),
        ];
        try
        {
            return StartServiceCtrlDispatcherW(table)
                ? _exitCode
                : (int)RecoveryOnceExitCode.InternalFailure;
        }
        finally
        {
            _stop.Dispose();
            _serviceMain = null;
            _serviceMainCallback = null;
            _controlHandler = null;
        }
    }

    private void ServiceMain(uint argumentCount, nint arguments)
    {
        nint statusHandle = RegisterServiceCtrlHandlerExW(
            RecoveryReadinessPolicy.ServiceName,
            _controlHandler!,
            nint.Zero);
        if (statusHandle == nint.Zero)
        {
            _exitCode = (int)RecoveryOnceExitCode.InternalFailure;
            return;
        }

        var reporter = new WindowsRecoveryServiceStatusReporter(statusHandle);
        try
        {
            _exitCode = _serviceMain!(reporter, _stop.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            _exitCode = (int)RecoveryOnceExitCode.InternalFailure;
            try
            {
                reporter.ReportAsync(
                    new RecoveryServiceStatusSnapshot(
                        RecoveryServiceState.Stopped,
                        false,
                        0,
                        TimeSpan.Zero,
                        BrokerErrorCodes.FSL_E_INTERNAL),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Win32Exception)
            {
            }
        }
    }

    private uint HandleControl(uint control, uint eventType, nint eventData, nint context)
    {
        if (control != ServiceControlStop)
        {
            return ErrorCallNotImplemented;
        }

        _stop.Cancel();
        return 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(uint argumentCount, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceControlHandler(
        uint control,
        uint eventType,
        nint eventData,
        nint context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct ServiceTableEntry(
        string? serviceName,
        ServiceMainCallback? serviceMain)
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        internal readonly string? ServiceName = serviceName;
        internal readonly ServiceMainCallback? ServiceMain = serviceMain;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW(
        [In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandlerExW(
        string serviceName,
        ServiceControlHandler handler,
        nint context);
}

internal sealed class WindowsRecoveryServiceStatusReporter(nint statusHandle)
    : IRecoveryServiceStatusReporter
{
    internal const uint ServiceStopped = 0x00000001;
    internal const uint ServiceStartPending = 0x00000002;
    internal const uint ServiceStopPending = 0x00000003;
    internal const uint ServiceRunning = 0x00000004;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAcceptStop = 0x00000001;

    public ValueTask ReportAsync(
        RecoveryServiceStatusSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        uint currentState = MapState(snapshot.State);
        bool pending = currentState is ServiceStartPending or ServiceStopPending;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = currentState,
            ControlsAccepted = currentState == ServiceRunning ? ServiceAcceptStop : 0,
            Win32ExitCode = 0,
            ServiceSpecificExitCode = 0,
            CheckPoint = pending ? checked((uint)Math.Max(1, snapshot.Checkpoint)) : 0,
            WaitHint = pending
                ? checked((uint)Math.Clamp(snapshot.WaitHint.TotalMilliseconds, 1, uint.MaxValue))
                : 0,
        };
        if (!SetServiceStatus(statusHandle, ref status))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return ValueTask.CompletedTask;
    }

    internal static uint MapState(RecoveryServiceState state) => state switch
    {
        RecoveryServiceState.StartPending
            or RecoveryServiceState.Preflight
            or RecoveryServiceState.Scanning => ServiceStartPending,
        RecoveryServiceState.Ready
            or RecoveryServiceState.RecoveryBlocked => ServiceRunning,
        RecoveryServiceState.Stopping => ServiceStopPending,
        RecoveryServiceState.Stopped => ServiceStopped,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint statusHandle, ref ServiceStatus serviceStatus);
}
