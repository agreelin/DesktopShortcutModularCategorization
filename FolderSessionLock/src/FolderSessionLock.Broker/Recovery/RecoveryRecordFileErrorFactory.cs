using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Recovery;

internal static class RecoveryRecordFileErrorFactory
{
    internal static Error Create(string code) => new(
        code,
        Message(code),
        ErrorCategory.UnrecoverableError);

    internal static BrokerError CreateProtocol(string code) => new(
        code,
        Message(code),
        false,
        null);

    internal static string Message(string code) => code switch
    {
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_SERVICE_SID_UNAVAILABLE =>
            "The recovery service identity could not be resolved.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_OWNER_PRIVILEGE_UNAVAILABLE =>
            "The recovery file owner could not be assigned securely.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED =>
            "The recovery file security privilege could not be restored.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_OWNER_SET_FAILED =>
            "The recovery file owner could not be set.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_SET_FAILED =>
            "The recovery file permissions could not be set.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_SECURITY_READ_FAILED =>
            "The recovery file security information could not be read.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED =>
            "The recovery file identity could not be read.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_OWNER_MISMATCH =>
            "The recovery file owner is not trusted.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_MISSING =>
            "The recovery file permissions are missing.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_NULL =>
            "The recovery file permissions are unsafe.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_INHERITANCE_INVALID =>
            "The recovery file permissions must not be inherited.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_MISMATCH =>
            "The recovery file permissions do not match the required policy.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH =>
            "The recovery file identity changed during the operation.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_ALREADY_EXISTS =>
            "A recovery record with the same identifier already exists.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED =>
            "The platform cannot safely replace the recovery record.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED =>
            "The recovery record could not be replaced atomically.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_HANDLE_DELETE_UNSUPPORTED =>
            "The platform cannot safely delete the recovery record.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_DELETE_FAILED =>
            "The recovery record could not be deleted.",
        BrokerErrorCodes.FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED =>
            "The committed recovery record could not be verified.",
        BrokerErrorCodes.FSL_E_RECOVERY_TEMP_CLEANUP_FAILED =>
            "A temporary recovery file could not be removed safely.",
        BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID =>
            "A recovery artifact does not satisfy the required security policy.",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}
