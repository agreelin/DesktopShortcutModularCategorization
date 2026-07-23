using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using FolderSessionLock.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Recovery;

internal interface IRecoveryStoreMutex
{
    ValueTask<RecoveryStoreMutexLease> AcquireAsync(CancellationToken cancellationToken);
}

internal sealed class RecoveryStoreMutexLease : IDisposable
{
    private Mutex? _mutex;

    internal RecoveryStoreMutexLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    public void Dispose()
    {
        Mutex? mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is not null)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}

internal sealed class RecoveryStoreMutex : IRecoveryStoreMutex
{
    internal const string ProductionName = @"Global\FolderSessionLock.RecoveryStore.v1";
    private readonly Func<Mutex> _factory;

    private RecoveryStoreMutex(Func<Mutex> factory)
    {
        _factory = factory;
    }

    internal static RecoveryStoreMutex CreateProduction() => new(CreateProtectedProductionMutex);

    internal static RecoveryStoreMutex CreateForTest(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.StartsWith(@"Global\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Tests must not use the production mutex namespace.", nameof(name));
        }

        return new RecoveryStoreMutex(() => new Mutex(false, name));
    }

    public async ValueTask<RecoveryStoreMutexLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        Mutex mutex = _factory();
        try
        {
            try
            {
                while (!mutex.WaitOne(TimeSpan.FromMilliseconds(50)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
            }
            catch (AbandonedMutexException)
            {
            }

            return new RecoveryStoreMutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static Mutex CreateProtectedProductionMutex()
    {
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        SecurityIdentifier service = WindowsServiceSid.RecoveryService;
        string sddl = $"D:P(A;;GA;;;{system.Value})(A;;GA;;;{administrators.Value})(A;;GA;;;{service.Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                1,
                out nint descriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = descriptor,
            };
            SafeWaitHandle handle = CreateMutexEx(
                ref attributes,
                ProductionName,
                0,
                0x001F0001);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var mutex = new Mutex();
            mutex.SafeWaitHandle = handle;
            return mutex;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal nint SecurityDescriptor;
        internal int InheritHandle;
    }

    [DllImport(
        "advapi32.dll",
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexEx(
        ref SecurityAttributes mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
