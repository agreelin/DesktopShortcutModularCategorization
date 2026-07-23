using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Protocol;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Security;

internal class WindowsProtectedPathSecurityPlatform
{
    internal virtual bool DirectoryExists(string path) => Directory.Exists(path);

    internal virtual uint GetDriveType(string rootPath) => NativeMethods.GetDriveType(rootPath);

    internal virtual Result<SafeFileHandle> OpenDirectory(string path)
    {
        SafeFileHandle handle = NativeMethods.CreateFile(
            path,
            NativeMethods.FileReadAttributes | NativeMethods.ReadControl,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            nint.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            nint.Zero);
        if (!handle.IsInvalid)
        {
            return Result<SafeFileHandle>.Success(handle);
        }

        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        string code = error is NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound
            ? BrokerErrorCodes.FSL_E_PROTECTED_PATH_NOT_FOUND
            : BrokerErrorCodes.FSL_E_PROTECTED_PATH_OPEN_FAILED;
        return Failure<SafeFileHandle>(code);
    }

    internal virtual Result<SafeFileHandle> OpenFile(string path)
    {
        SafeFileHandle handle = NativeMethods.CreateFile(
            path,
            NativeMethods.FileReadData | NativeMethods.FileReadAttributes | NativeMethods.ReadControl,
            NativeMethods.FileShareRead,
            nint.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOpenReparsePoint,
            nint.Zero);
        if (!handle.IsInvalid)
        {
            return Result<SafeFileHandle>.Success(handle);
        }

        handle.Dispose();
        return Failure<SafeFileHandle>(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_NOT_FOUND);
    }

    internal virtual Result<NativeMethods.FileAttributeTagInfo> GetAttributes(SafeFileHandle handle) =>
        NativeMethods.GetFileAttributeTagInfo(
            handle,
            NativeMethods.FileInfoByHandleClass.FileAttributeTagInfo,
            out NativeMethods.FileAttributeTagInfo information,
            (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInfo>()) != 0
                ? Result<NativeMethods.FileAttributeTagInfo>.Success(information)
                : Failure<NativeMethods.FileAttributeTagInfo>(
                    BrokerErrorCodes.FSL_E_PROTECTED_PATH_OPEN_FAILED);

    internal virtual unsafe Result<string> GetFinalPath(SafeFileHandle handle)
    {
        uint capacity = 260;
        while (true)
        {
            var buffer = new char[capacity];
            fixed (char* pointer = buffer)
            {
                uint length = NativeMethods.GetFinalPathNameByHandle(handle, pointer, capacity, 0);
                if (length == 0)
                {
                    return Failure<string>(BrokerErrorCodes.FSL_E_PROTECTED_PATH_OPEN_FAILED);
                }

                if (length < capacity)
                {
                    return Result<string>.Success(NormalizeFinalPath(new string(pointer, 0, (int)length)));
                }

                capacity = checked(length + 1);
            }
        }
    }

    internal virtual unsafe Result<string> GetFileSystemName(SafeFileHandle handle)
    {
        var buffer = new char[64];
        fixed (char* pointer = buffer)
        {
            if (NativeMethods.GetVolumeInformationByHandle(
                    handle,
                    nint.Zero,
                    0,
                    out _,
                    out _,
                    out _,
                    pointer,
                    (uint)buffer.Length) == 0)
            {
                return Failure<string>(BrokerErrorCodes.FSL_E_PROTECTED_PATH_VOLUME_UNSUPPORTED);
            }

            return Result<string>.Success(new string(pointer));
        }
    }

    internal virtual unsafe Result<DirectoryIdentity> GetIdentity(SafeFileHandle handle)
    {
        if (NativeMethods.GetFileIdInfo(
                handle,
                NativeMethods.FileInfoByHandleClass.FileIdInfo,
                out NativeMethods.FileIdInfo information,
                (uint)Marshal.SizeOf<NativeMethods.FileIdInfo>()) == 0)
        {
            return Failure<DirectoryIdentity>(
                BrokerErrorCodes.FSL_E_PROTECTED_PATH_IDENTITY_UNAVAILABLE);
        }

        byte* identifier = information.FileId.Identifier;
        return Result<DirectoryIdentity>.Success(DirectoryIdentity.FromFileId(
            information.VolumeSerialNumber,
            new ReadOnlySpan<byte>(identifier, 16)));
    }

    internal virtual Result<ProtectedPathSecurityDescriptor> ReadSecurity(SafeFileHandle handle)
    {
        uint error = NativeMethods.GetSecurityInfo(
            handle,
            NativeMethods.SeObjectType.FileObject,
            NativeMethods.OwnerSecurityInformation | NativeMethods.DaclSecurityInformation,
            out nint owner,
            out _,
            out nint dacl,
            out _,
            out nint securityDescriptor);
        if (error != NativeMethods.ErrorSuccess)
        {
            return Failure<ProtectedPathSecurityDescriptor>(
                BrokerErrorCodes.FSL_E_PROTECTED_PATH_SECURITY_READ_FAILED);
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
                return Failure<ProtectedPathSecurityDescriptor>(
                    BrokerErrorCodes.FSL_E_PROTECTED_PATH_SECURITY_READ_FAILED);
            }

            var control = (ControlFlags)controlValue;
            bool present = (control & ControlFlags.DiscretionaryAclPresent) != 0;
            bool isNull = present && dacl == nint.Zero;
            if (!present || isNull)
            {
                return Result<ProtectedPathSecurityDescriptor>.Success(new(
                    new SecurityIdentifier(owner).Value,
                    present,
                    isNull,
                    control,
                    []));
            }

            if (NativeMethods.IsValidAcl(dacl) == 0
                || NativeMethods.GetAclInformation(
                    dacl,
                    out NativeMethods.AclSizeInformation size,
                    (uint)Marshal.SizeOf<NativeMethods.AclSizeInformation>(),
                    NativeMethods.AclInformationClass.AclSizeInformation) == 0
                || size.AclBytesInUse == 0
                || size.AclBytesInUse > int.MaxValue)
            {
                return Failure<ProtectedPathSecurityDescriptor>(
                    BrokerErrorCodes.FSL_E_PROTECTED_PATH_SECURITY_READ_FAILED);
            }

            var binary = new byte[size.AclBytesInUse];
            Marshal.Copy(dacl, binary, 0, binary.Length);
            RawAcl rawAcl;
            try
            {
                rawAcl = new RawAcl(binary, 0);
            }
            catch (ArgumentException)
            {
                return Failure<ProtectedPathSecurityDescriptor>(
                    BrokerErrorCodes.FSL_E_PROTECTED_PATH_SECURITY_READ_FAILED);
            }

            var aces = new List<ProtectedPathAce>();
            for (int index = 0; index < rawAcl.Count; index++)
            {
                GenericAce genericAce = rawAcl[index];
                if (genericAce is not QualifiedAce ace)
                {
                    aces.Add(new ProtectedPathAce(
                        false,
                        string.Empty,
                        0,
                        ToInheritanceFlags(genericAce.AceFlags),
                        ToPropagationFlags(genericAce.AceFlags),
                        (genericAce.AceFlags & AceFlags.Inherited) != 0,
                        genericAce.AceType,
                        null,
                        false,
                        false,
                        false,
                        genericAce.AceFlags));
                    continue;
                }

                bool isAllow = ace.AceQualifier == AceQualifier.AccessAllowed;
                aces.Add(new ProtectedPathAce(
                    isAllow,
                    ace.SecurityIdentifier.Value,
                    ace.AccessMask,
                    ToInheritanceFlags(ace.AceFlags),
                    ToPropagationFlags(ace.AceFlags),
                    (ace.AceFlags & AceFlags.Inherited) != 0,
                    ace.AceType,
                    ace.AceQualifier,
                    ace.IsCallback,
                    ace is ObjectAce,
                    true,
                    ace.AceFlags));
            }

            return Result<ProtectedPathSecurityDescriptor>.Success(new(
                new SecurityIdentifier(owner).Value,
                present,
                isNull,
                control,
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

    private static InheritanceFlags ToInheritanceFlags(AceFlags flags) =>
        ((flags & AceFlags.ContainerInherit) != 0 ? InheritanceFlags.ContainerInherit : 0)
        | ((flags & AceFlags.ObjectInherit) != 0 ? InheritanceFlags.ObjectInherit : 0);

    private static PropagationFlags ToPropagationFlags(AceFlags flags) =>
        ((flags & AceFlags.InheritOnly) != 0 ? PropagationFlags.InheritOnly : 0)
        | ((flags & AceFlags.NoPropagateInherit) != 0 ? PropagationFlags.NoPropagateInherit : 0);

    private static string NormalizeFinalPath(string finalPath)
    {
        const string localPrefix = "\\\\?\\";
        const string uncPrefix = "\\\\?\\UNC\\";
        string dosPath = finalPath.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"\\\\{finalPath[uncPrefix.Length..]}"
            : finalPath.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
                ? finalPath[localPrefix.Length..]
                : finalPath;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dosPath));
    }

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(new Error(
        code,
        code,
        ErrorCategory.UnrecoverableError));
}
