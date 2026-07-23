using System.Diagnostics.CodeAnalysis;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Security;

public sealed record BrokerAuthenticatedClient(
    uint ProcessId,
    DateTimeOffset ProcessStartUtc,
    SessionIdentity ClientIdentity,
    SessionIdentity BrokerIdentity);

public sealed record BrokerAuthenticationResult(
    BrokerAuthenticatedClient? Client,
    BrokerError? Error)
{
    public bool IsSuccess => Client is not null;

    public static BrokerAuthenticationResult Success(BrokerAuthenticatedClient client) => new(client, null);

    public static BrokerAuthenticationResult Failure(BrokerError error) => new(null, error);
}

public interface IBrokerConnectionAuthenticator
{
    ValueTask<BrokerAuthenticationResult> AuthenticateAsync(
        Stream stream,
        BrokerClientHello hello,
        BrokerConsentOptions options,
        CancellationToken cancellationToken = default);
}

internal interface IBrokerProcessTerminator
{
    [DoesNotReturn]
    void TerminateAfterIdentityRestoreFailure();
}

internal sealed class FailFastBrokerProcessTerminator : IBrokerProcessTerminator
{
    [DoesNotReturn]
    public void TerminateAfterIdentityRestoreFailure() =>
        Environment.FailFast("The broker could not restore its Windows identity.");
}
