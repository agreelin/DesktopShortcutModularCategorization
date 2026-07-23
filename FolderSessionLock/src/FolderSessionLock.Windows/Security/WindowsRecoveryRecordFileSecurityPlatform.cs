using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Security;

internal class WindowsRecoveryRecordFileSecurityPlatform
{
    internal virtual Result<SecurityIdentifier> ResolveServiceSid() =>
        Result<SecurityIdentifier>.Success(WindowsServiceSid.RecoveryService);

    internal virtual unsafe Result<RecoveryRecordFileSecurityEvidence> Read(
        SafeFileHandle fileHandle)
    {
        if (NativeMethods.GetFileIdInfo(
                fileHandle,
                NativeMethods.FileInfoByHandleClass.FileIdInfo,
                out NativeMethods.FileIdInfo fileId,
                (uint)Marshal.SizeOf<NativeMethods.FileIdInfo>()) == 0
            || NativeMethods.GetFileStandardInfo(
                fileHandle,
                NativeMethods.FileInfoByHandleClass.FileStandardInfo,
                out NativeMethods.FileStandardInfo standard,
                (uint)Marshal.SizeOf<NativeMethods.FileStandardInfo>()) == 0)
        {
            return Failure<RecoveryRecordFileSecurityEvidence>(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED);
        }

        uint error = NativeMethods.GetSecurityInfo(
            fileHandle,
            NativeMethods.SeObjectType.FileObject,
            NativeMethods.OwnerSecurityInformation | NativeMethods.DaclSecurityInformation,
            out nint owner,
            out _,
            out nint dacl,
            out _,
            out nint securityDescriptor);
        if (error != NativeMethods.ErrorSuccess)
        {
            return Failure<RecoveryRecordFileSecurityEvidence>(
                BrokerErrorCodes.FSL_E_RECOVERY_FILE_SECURITY_READ_FAILED);
        }

        try
        {
            if (securityDescriptor == nint.Zero
                || owner == nint.Zero
                || NativeMethods.IsValidSid(owner) == 0
                || NativeMethods.GetSecurityDescriptorControl(
                    securityDescriptor,
                    out ushort controlValue,
                    out _) == 0)
            {
                return Failure<RecoveryRecordFileSecurityEvidence>(
                    BrokerErrorCodes.FSL_E_RECOVERY_FILE_SECURITY_READ_FAILED);
            }

            var control = (ControlFlags)controlValue;
            bool present = (control & ControlFlags.DiscretionaryAclPresent) != 0;
            bool isNull = present && dacl == nint.Zero;
            byte revision = 0;
            var aces = new List<RecoveryRecordFileAce>();
            if (present && !isNull)
            {
                if (NativeMethods.IsValidAcl(dacl) == 0
                    || NativeMethods.GetAclInformation(
                        dacl,
                        out NativeMethods.AclSizeInformation size,
                        (uint)Marshal.SizeOf<NativeMethods.AclSizeInformation>(),
                        NativeMethods.AclInformationClass.AclSizeInformation) == 0
                    || size.AclBytesInUse < Marshal.SizeOf<NativeMethods.AclHeader>())
                {
                    return Failure<RecoveryRecordFileSecurityEvidence>(
                        BrokerErrorCodes.FSL_E_RECOVERY_FILE_SECURITY_READ_FAILED);
                }

                revision = Marshal.PtrToStructure<NativeMethods.AclHeader>(dacl).AclRevision;
                for (uint index = 0; index < size.AceCount; index++)
                {
                    if (NativeMethods.GetAce(dacl, index, out nint acePointer) == 0)
                    {
                        return Failure<RecoveryRecordFileSecurityEvidence>(
                            BrokerErrorCodes.FSL_E_RECOVERY_FILE_SECURITY_READ_FAILED);
                    }

                    GenericAce generic;
                    try
                    {
                        NativeMethods.AceHeader header = Marshal.PtrToStructure<NativeMethods.AceHeader>(
                            acePointer);
                        var bytes = new byte[header.AceSize];
                        Marshal.Copy(acePointer, bytes, 0, bytes.Length);
                        generic = GenericAce.CreateFromBinaryForm(bytes, 0);
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException or OverflowException)
                    {
                        return Failure<RecoveryRecordFileSecurityEvidence>(
                            BrokerErrorCodes.FSL_E_RECOVERY_FILE_SECURITY_READ_FAILED);
                    }

                    aces.Add(ToAce(generic));
                }
            }

            byte* identifier = fileId.FileId.Identifier;
            var identity = new RecoveryRecordFileIdentity(
                fileId.VolumeSerialNumber,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    new ReadOnlySpan<byte>(identifier + 8, 8)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    new ReadOnlySpan<byte>(identifier, 8)),
                standard.NumberOfLinks);
            return Result<RecoveryRecordFileSecurityEvidence>.Success(new(
                identity,
                new SecurityIdentifier(owner).Value,
                present,
                isNull,
                (control & ControlFlags.DiscretionaryAclProtected) != 0,
                revision,
                aces));
        }
        finally
        {
            if (securityDescriptor != nint.Zero)
            {
                NativeMethods.LocalFree(securityDescriptor);
            }
        }
    }

    internal virtual Result SetOwner(SafeFileHandle fileHandle, SecurityIdentifier owner)
    {
        byte[] ownerBytes = new byte[owner.BinaryLength];
        owner.GetBinaryForm(ownerBytes, 0);
        unsafe
        {
            fixed (byte* ownerPointer = ownerBytes)
            {
                return NativeMethods.SetSecurityInfo(
                        fileHandle,
                        NativeMethods.SeObjectType.FileObject,
                        NativeMethods.OwnerSecurityInformation,
                        (nint)ownerPointer,
                        nint.Zero,
                        nint.Zero,
                        nint.Zero) == NativeMethods.ErrorSuccess
                    ? Result.Success()
                    : Failure(BrokerErrorCodes.FSL_E_RECOVERY_FILE_OWNER_SET_FAILED);
            }
        }
    }

    internal virtual Result SetDacl(
        SafeFileHandle fileHandle,
        SecurityIdentifier serviceSid)
    {
        var acl = new RawAcl(2, 3);
        acl.InsertAce(0, Allow(ProtectedPathAclPolicy.SystemSid));
        acl.InsertAce(1, Allow(ProtectedPathAclPolicy.AdministratorsSid));
        acl.InsertAce(2, Allow(serviceSid.Value));
        byte[] bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        unsafe
        {
            fixed (byte* daclPointer = bytes)
            {
                return NativeMethods.SetSecurityInfo(
                        fileHandle,
                        NativeMethods.SeObjectType.FileObject,
                        NativeMethods.DaclSecurityInformation
                            | NativeMethods.ProtectedDaclSecurityInformation,
                        nint.Zero,
                        nint.Zero,
                        (nint)daclPointer,
                        nint.Zero) == NativeMethods.ErrorSuccess
                    ? Result.Success()
                    : Failure(BrokerErrorCodes.FSL_E_RECOVERY_FILE_DACL_SET_FAILED);
            }
        }
    }

    private static RecoveryRecordFileAce ToAce(GenericAce generic)
    {
        if (generic is QualifiedAce qualified)
        {
            return new RecoveryRecordFileAce(
                generic.AceType,
                generic.AceFlags,
                qualified.AccessMask,
                qualified.SecurityIdentifier.Value,
                qualified.AceQualifier,
                qualified.IsCallback,
                qualified is ObjectAce,
                true);
        }

        return new RecoveryRecordFileAce(
            generic.AceType,
            generic.AceFlags,
            0,
            string.Empty,
            null,
            false,
            false,
            false);
    }

    private static CommonAce Allow(string sid) => new(
        AceFlags.None,
        AceQualifier.AccessAllowed,
        0x001F01FF,
        new SecurityIdentifier(sid),
        isCallback: false,
        opaque: null);

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(new Error(
        code,
        code,
        ErrorCategory.UnrecoverableError));

    private static Result Failure(string code) => Result.Failure(new Error(
        code,
        code,
        ErrorCategory.UnrecoverableError));
}

internal sealed record RecoveryRecordFileSecurityEvidence(
    RecoveryRecordFileIdentity Identity,
    string OwnerSid,
    bool DaclPresent,
    bool DaclIsNull,
    bool DaclProtected,
    byte AclRevision,
    IReadOnlyList<RecoveryRecordFileAce> Aces);

internal sealed record RecoveryRecordFileAce(
    AceType AceType,
    AceFlags AceFlags,
    int AccessMask,
    string Sid,
    AceQualifier? AceQualifier,
    bool IsCallback,
    bool IsObject,
    bool IsQualified);
