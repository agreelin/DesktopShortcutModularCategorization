using System.Globalization;
using FolderSessionLock.Broker.Transport;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Security;

public sealed record BrokerConsentOptions(
    string PipeName,
    uint SessionId,
    Guid RequestId,
    uint ClientProcessId,
    ulong ClientProcessCreationFileTime)
{
    public static bool TryParse(IReadOnlyList<string> arguments, out BrokerConsentOptions? options)
    {
        options = null;
        if (arguments.Count != 12
            || arguments[0] != "--mode"
            || arguments[1] != "consent-broker"
            || arguments[2] != "--pipe-name"
            || arguments[3] != "FolderSessionLock.Broker.v1"
            || arguments[4] != "--session-id"
            || !uint.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out uint sessionId)
            || arguments[6] != "--request-id"
            || !Guid.TryParseExact(arguments[7], "D", out Guid requestId)
            || requestId == Guid.Empty
            || arguments[7] != requestId.ToString("D")
            || arguments[8] != "--client-process-id"
            || !uint.TryParse(arguments[9], NumberStyles.None, CultureInfo.InvariantCulture, out uint clientProcessId)
            || clientProcessId == 0
            || arguments[9] != clientProcessId.ToString(CultureInfo.InvariantCulture)
            || arguments[10] != "--client-process-creation-filetime"
            || !ulong.TryParse(
                arguments[11],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong clientProcessCreationFileTime)
            || clientProcessCreationFileTime == 0
            || arguments[11] != clientProcessCreationFileTime.ToString(CultureInfo.InvariantCulture))
        {
            return false;
        }

        options = new BrokerConsentOptions(
            BrokerPipeEndpoint.PipeName,
            sessionId,
            requestId,
            clientProcessId,
            clientProcessCreationFileTime);
        return true;
    }
}
