using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Recovery;

internal sealed record RecoveryRecordError(string Code, string? Field = null);

internal static class RecoveryRecordErrors
{
    internal static RecoveryRecordError MagicInvalid { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_MAGIC_INVALID);
    internal static RecoveryRecordError VersionUnsupported { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_VERSION_UNSUPPORTED, "containerVersion");
    internal static RecoveryRecordError FlagsUnsupported { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_FLAGS_UNSUPPORTED, "flags");
    internal static RecoveryRecordError LengthInvalid { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_LENGTH_INVALID, "protectedPayloadLength");
    internal static RecoveryRecordError Truncated { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_TRUNCATED);
    internal static RecoveryRecordError TrailingData { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_TRAILING_DATA);
    internal static RecoveryRecordError UnprotectFailed { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_UNPROTECT_FAILED);
    internal static RecoveryRecordError PayloadTooLarge { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_PAYLOAD_TOO_LARGE);
    internal static RecoveryRecordError PayloadMalformed { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_PAYLOAD_MALFORMED);
    internal static RecoveryRecordError PayloadSchemaInvalid(string? field = null) =>
        new(BrokerErrorCodes.FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID, field);
    internal static RecoveryRecordError PayloadVersionUnsupported { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_PAYLOAD_VERSION_UNSUPPORTED, "schemaVersion");
    internal static RecoveryRecordError PayloadStateInvalid { get; } =
        new(BrokerErrorCodes.FSL_E_RECOVERY_PAYLOAD_STATE_INVALID, "state");
}

internal sealed record RecoveryRecordReadResult(
    RecoveryRecord? Record,
    RecoveryRecordError? Error)
{
    internal bool IsSuccess => Record is not null;

    internal static RecoveryRecordReadResult Success(RecoveryRecord record) => new(record, null);

    internal static RecoveryRecordReadResult Failure(RecoveryRecordError error) => new(null, error);
}
