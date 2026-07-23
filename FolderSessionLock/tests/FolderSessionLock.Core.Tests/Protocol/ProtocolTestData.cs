using System.Text;
using System.Text.Json;
using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Protocol.Tests;

internal static class ProtocolTestData
{
    public static readonly DateTimeOffset ServerTimeUtc =
        new(2026, 7, 19, 16, 30, 0, TimeSpan.Zero);

    public static readonly Guid RequestId =
        Guid.ParseExact("11111111-2222-3333-4444-555555555555", "D");

    public static readonly Guid TaskId =
        Guid.ParseExact("a0b1c2d3-e4f5-4678-9123-abcdefabcdef", "D");

    public static readonly Guid RecoveryRecordId =
        Guid.ParseExact("12345678-1234-4678-9123-abcdefabcdef", "D");

    public static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromMinutes(1), TimeSpan.FromHours(8)).Value;

    public static string Request(string command, string payload, string protocolVersion = "1") => $$"""
        {
          "protocolVersion": {{protocolVersion}},
          "requestId": "{{RequestId:D}}",
          "command": "{{command}}",
          "clientSessionId": 1,
          "sentAtUtc": "2026-07-19T16:30:00.0000000Z",
          "payload": {{payload}}
        }
        """;

    public static BrokerRequestParseResult ParseRequest(string json) =>
        BrokerProtocolJson.DeserializeRequest(Utf8(json), ServerTimeUtc, DurationPolicy);

    public static BrokerResponseParseResult ParseResponse(string json) =>
        BrokerProtocolJson.DeserializeResponse(Utf8(json));

    public static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);

    public static string JsonString(string value) => JsonSerializer.Serialize(value);
}
