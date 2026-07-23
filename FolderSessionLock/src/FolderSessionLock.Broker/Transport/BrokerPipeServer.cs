using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Broker.Transport;

public static class BrokerPipeServer
{
    public static readonly TimeSpan ClientConnectTimeout = TimeSpan.FromSeconds(15);
    private const PipeAccessRights AllowedRights = PipeAccessRights.ReadWrite;
    private const int BufferSize = BrokerPipeEndpoint.MaximumBodyLength + BrokerPipeEndpoint.LengthPrefixSize;
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const int ErrorAccessDenied = 5;

    public static PipeSecurity CreateSecurity(
        SecurityIdentifier initiatingLogonSid,
        SecurityIdentifier brokerSid)
    {
        ArgumentNullException.ThrowIfNull(initiatingLogonSid);
        ArgumentNullException.ThrowIfNull(brokerSid);

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            initiatingLogonSid,
            AllowedRights,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            brokerSid,
            AllowedRights,
            AccessControlType.Allow));
        return security;
    }

    public static NamedPipeServerStream Create(
        SecurityIdentifier initiatingLogonSid,
        SecurityIdentifier brokerSid)
    {
        byte[] descriptor = CreateSecurity(initiatingLogonSid, brokerSid)
            .GetSecurityDescriptorBinaryForm();
        GCHandle descriptorHandle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var securityAttributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = descriptorHandle.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            SafePipeHandle pipeHandle = CreateNamedPipe(
                BrokerPipeEndpoint.LocalPath,
                PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped,
                PipeRejectRemoteClients,
                maxInstances: 1,
                outBufferSize: BufferSize,
                inBufferSize: BufferSize,
                defaultTimeout: 0,
                ref securityAttributes);
            if (pipeHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                pipeHandle.Dispose();
                if (error == ErrorAccessDenied)
                {
                    throw new UnauthorizedAccessException("Access to the broker pipe was denied.");
                }

                throw new IOException(
                    "The broker pipe could not be created.",
                    new Win32Exception(error));
            }

            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    pipeHandle);
            }
            catch
            {
                pipeHandle.Dispose();
                throw;
            }
        }
        finally
        {
            descriptorHandle.Free();
        }
    }

    public static async ValueTask<BrokerPipeConnectionResult> RunOnceAsync(
        SecurityIdentifier initiatingLogonSid,
        SecurityIdentifier brokerSid,
        BrokerConsentOptions options,
        LockDurationPolicy durationPolicy,
        IClock clock,
        IBrokerConnectionAuthenticator authenticator,
        IReplayRegistry replayRegistry,
        Func<BrokerRequestEnvelope, CancellationToken, ValueTask<BrokerExecutionOutcome>> processRequest,
        CancellationToken cancellationToken = default,
        TimeSpan? clientConnectTimeout = null)
    {
        try
        {
            await using NamedPipeServerStream pipe = Create(initiatingLogonSid, brokerSid);
            return await RunCreatedOnceAsync(
                pipe,
                options,
                durationPolicy,
                clock,
                authenticator,
                replayRegistry,
                processRequest,
                cancellationToken,
                clientConnectTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new BrokerPipeConnectionResult(false, BrokerPipeFrameCodec.CancelledError());
        }
        catch (UnauthorizedAccessException)
        {
            return new BrokerPipeConnectionResult(false, PipeInitializationError());
        }
        catch (IOException)
        {
            return new BrokerPipeConnectionResult(false, PipeInitializationError());
        }
    }

    internal static async ValueTask<BrokerPipeConnectionResult> RunCreatedOnceAsync(
        NamedPipeServerStream pipe,
        BrokerConsentOptions options,
        LockDurationPolicy durationPolicy,
        IClock clock,
        IBrokerConnectionAuthenticator authenticator,
        IReplayRegistry replayRegistry,
        Func<BrokerRequestEnvelope, CancellationToken, ValueTask<BrokerExecutionOutcome>> processRequest,
        CancellationToken cancellationToken = default,
        TimeSpan? clientConnectTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        try
        {
            using var connectCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCancellation.CancelAfter(clientConnectTimeout ?? ClientConnectTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(connectCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new BrokerPipeConnectionResult(false, ConnectTimeoutError());
            }

            return await BrokerPipeConnection.ProcessAsync(
                pipe,
                options,
                durationPolicy,
                clock,
                authenticator,
                replayRegistry,
                processRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new BrokerPipeConnectionResult(false, BrokerPipeFrameCodec.CancelledError());
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return new BrokerPipeConnectionResult(false, BrokerPipeFrameCodec.MalformedError());
        }
    }

    private static BrokerError PipeInitializationError() => new(
        BrokerErrorCodes.FSL_E_PIPE_INITIALIZATION_FAILED,
        "The elevated broker could not create its secure communication endpoint.",
        false,
        null);

    private static BrokerError ConnectTimeoutError() => new(
        BrokerErrorCodes.FSL_E_BROKER_CONNECT_TIMEOUT,
        "The elevated broker did not establish a secure connection in time.",
        true,
        null);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(
        string pipeName,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);
}
