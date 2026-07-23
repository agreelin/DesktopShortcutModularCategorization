using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using FolderSessionLock.Broker.Security;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Services;

namespace FolderSessionLock.Broker.Transport.Tests;

public sealed class WindowsBrokerConnectionAuthenticatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AuthenticateAsync_ReadsActualPipeProcessAndTokenIdentity()
    {
        await using ConnectedPipe pipe = await ConnectedPipe.Create().WaitAsync(TestTimeout);
        ResultIdentity identity = await CurrentIdentity().WaitAsync(TestTimeout);
        using Process process = Process.GetCurrentProcess();
        var hello = CreateHello(checked((uint)process.Id), checked((uint)identity.Value.WindowsSessionId));
        var options = new BrokerConsentOptions(
            "FolderSessionLock.Broker.v1",
            hello.ClientSessionId,
            hello.RequestId,
            1234,
            133970112000000000);
        await EstablishClientSecurityContext(pipe).WaitAsync(TestTimeout);

        BrokerAuthenticationResult result = await new WindowsBrokerConnectionAuthenticator()
            .AuthenticateAsync(pipe.Server, hello, options)
            .AsTask()
            .WaitAsync(TestTimeout);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(checked((uint)process.Id), result.Client!.ProcessId);
        Assert.Equal(identity.Value, result.Client.ClientIdentity);
        Assert.Equal(identity.Value, result.Client.BrokerIdentity);
    }

    [Fact]
    public async Task AuthenticateAsync_ClaimedPidMismatchUsesExactError()
    {
        await using ConnectedPipe pipe = await ConnectedPipe.Create().WaitAsync(TestTimeout);
        ResultIdentity identity = await CurrentIdentity().WaitAsync(TestTimeout);
        using Process process = Process.GetCurrentProcess();
        var hello = CreateHello(checked((uint)process.Id + 1), checked((uint)identity.Value.WindowsSessionId));
        await EstablishClientSecurityContext(pipe).WaitAsync(TestTimeout);

        BrokerAuthenticationResult result = await new WindowsBrokerConnectionAuthenticator()
            .AuthenticateAsync(
                pipe.Server,
                hello,
                new BrokerConsentOptions(
                    "FolderSessionLock.Broker.v1",
                    hello.ClientSessionId,
                    hello.RequestId,
                    1234,
                    133970112000000000))
            .AsTask()
            .WaitAsync(TestTimeout);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrokerErrorCodes.FSL_E_CLIENT_PROCESS_MISMATCH, result.Error!.Code);
        Assert.Equal("The connected client process does not match the handshake.", result.Error.Message);
        Assert.False(result.Error.Retryable);
        Assert.Equal("claimedClientProcessId", result.Error.Field);
    }

    [Fact]
    public async Task AuthenticateAsync_RevertFailureTerminatesBeforeBrokerIdentityProcessing()
    {
        await using ConnectedPipe pipe = await ConnectedPipe.Create().WaitAsync(TestTimeout);
        ResultIdentity identity = await CurrentIdentity().WaitAsync(TestTimeout);
        using Process process = Process.GetCurrentProcess();
        var hello = CreateHello(checked((uint)process.Id), checked((uint)identity.Value.WindowsSessionId));
        await EstablishClientSecurityContext(pipe).WaitAsync(TestTimeout);
        var terminator = new ThrowingProcessTerminator();
        bool continued = false;

        await Assert.ThrowsAsync<TestProcessTerminationException>(async () =>
        {
            await new WindowsBrokerConnectionAuthenticator(terminator, () =>
                {
                    Assert.True(RevertToSelfForTest());
                    return false;
                })
                .AuthenticateAsync(
                    pipe.Server,
                    hello,
                    new BrokerConsentOptions(
                        "FolderSessionLock.Broker.v1",
                        hello.ClientSessionId,
                        hello.RequestId,
                        1234,
                        133970112000000000));
            continued = true;
        });

        Assert.True(terminator.Called);
        Assert.False(continued);
    }

    private static BrokerClientHello CreateHello(uint processId, uint sessionId) => new(
        BrokerFrameType.ClientHello,
        1,
        1,
        Guid.ParseExact("11111111-2222-3333-4444-555555555555", "D"),
        BrokerCommand.GetStatus,
        processId,
        sessionId,
        BrokerHandshakeBinding.CreateNonce(),
        DateTimeOffset.UtcNow);

    private static async Task EstablishClientSecurityContext(ConnectedPipe pipe)
    {
        byte[] buffer = new byte[1];
        Task<int> read = pipe.Server.ReadAsync(buffer).AsTask();
        await pipe.Client.WriteAsync(new byte[] { 1 });
        await pipe.Client.FlushAsync();
        Assert.Equal(1, await read);
    }

    private static async Task<ResultIdentity> CurrentIdentity()
    {
        var result = await new WindowsSessionIdentityProvider().GetCurrentAsync();
        Assert.True(result.IsSuccess, result.Error?.Message);
        return new ResultIdentity(result.Value);
    }

    private sealed record ResultIdentity(SessionIdentity Value);

    private sealed class ThrowingProcessTerminator : IBrokerProcessTerminator
    {
        internal bool Called { get; private set; }

        [DoesNotReturn]
        public void TerminateAfterIdentityRestoreFailure()
        {
            Called = true;
            throw new TestProcessTerminationException();
        }
    }

    private sealed class TestProcessTerminationException : Exception
    {
    }

    [DllImport("advapi32.dll", EntryPoint = "RevertToSelf", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelfForTest();

    private sealed class ConnectedPipe : IAsyncDisposable
    {
        private ConnectedPipe(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            Server = server;
            Client = client;
        }

        internal NamedPipeServerStream Server { get; }
        internal NamedPipeClientStream Client { get; }

        internal static async Task<ConnectedPipe> Create()
        {
            string name = $"FolderSessionLock.Tests.{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task waiting = server.WaitForConnectionAsync();
            await client.ConnectAsync(2_000);
            await waiting;
            return new ConnectedPipe(server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
