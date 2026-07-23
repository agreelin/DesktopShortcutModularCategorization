using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Interop;
using FolderSessionLock.Windows.Models;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Windows.Security;

public sealed class DirectoryAclEditor
{
    private readonly IDirectoryAclEditorTestHook? _testHook;
    private static readonly Error InvalidDaclError = new(
        "windows.acl.invalid_dacl",
        "The directory DACL is absent, null, invalid, or noncanonical.",
        ErrorCategory.UnrecoverableError);

    private static readonly Error ExistingAceError = new(
        "windows.acl.identical_ace_exists",
        "An identical explicit deny ACE already exists.",
        ErrorCategory.ValidationFailed);

    private static readonly Error VerificationError = new(
        "windows.acl.verification_failed",
        "The directory DACL did not match the required post-operation state.",
        ErrorCategory.UnrecoverableError);

    private static readonly Error RolledBackError = new(
        "windows.acl.add_verification_rolled_back",
        "The added ACE failed verification and was rolled back.",
        ErrorCategory.PlatformError);

    public DirectoryAclEditor()
    {
    }

    internal DirectoryAclEditor(IDirectoryAclEditorTestHook testHook)
    {
        _testHook = testHook ?? throw new ArgumentNullException(nameof(testHook));
    }

    public Result<DirectoryAclSnapshot> ReadSnapshot(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            return Result<DirectoryAclSnapshot>.Failure(InvalidDaclError);
        }

        uint error = NativeMethods.GetSecurityInfo(
            handle,
            NativeMethods.SeObjectType.FileObject,
            NativeMethods.OwnerSecurityInformation
                | NativeMethods.GroupSecurityInformation
                | NativeMethods.DaclSecurityInformation,
            out nint owner,
            out nint group,
            out nint dacl,
            out _,
            out nint securityDescriptor);
        if (error != NativeMethods.ErrorSuccess)
        {
            return NativeFailure<DirectoryAclSnapshot>("GetSecurityInfo", error);
        }

        try
        {
            if (owner == nint.Zero
                || group == nint.Zero
                || dacl == nint.Zero
                || NativeMethods.IsValidSid(owner) == 0
                || NativeMethods.IsValidSid(group) == 0
                || NativeMethods.IsValidAcl(dacl) == 0
                || NativeMethods.GetSecurityDescriptorControl(
                    securityDescriptor,
                    out ushort control,
                    out _) == 0
                || !HasPresentNonNullDacl((ControlFlags)control, dacl)
                || NativeMethods.GetAclInformation(
                    dacl,
                    out NativeMethods.AclSizeInformation size,
                    (uint)Marshal.SizeOf<NativeMethods.AclSizeInformation>(),
                    NativeMethods.AclInformationClass.AclSizeInformation) == 0
                || size.AclBytesInUse == 0
                || size.AclBytesInUse > int.MaxValue)
            {
                return Result<DirectoryAclSnapshot>.Failure(InvalidDaclError);
            }

            var daclBinary = new byte[size.AclBytesInUse];
            Marshal.Copy(dacl, daclBinary, 0, daclBinary.Length);

            RawAcl rawAcl;
            try
            {
                rawAcl = new RawAcl(daclBinary, 0);
                if (!new DiscretionaryAcl(true, false, rawAcl).IsCanonical)
                {
                    return Result<DirectoryAclSnapshot>.Failure(InvalidDaclError);
                }
            }
            catch (ArgumentException)
            {
                return Result<DirectoryAclSnapshot>.Failure(InvalidDaclError);
            }

            NativeMethods.AclHeader aclHeader = Marshal.PtrToStructure<NativeMethods.AclHeader>(dacl);
            if (aclHeader.AceCount != size.AceCount)
            {
                return Result<DirectoryAclSnapshot>.Failure(InvalidDaclError);
            }

            var aceBinaries = new byte[size.AceCount][];
            for (uint index = 0; index < size.AceCount; index++)
            {
                if (NativeMethods.GetAce(dacl, index, out nint acePointer) == 0
                    || acePointer == nint.Zero)
                {
                    return Result<DirectoryAclSnapshot>.Failure(InvalidDaclError);
                }

                NativeMethods.AceHeader aceHeader =
                    Marshal.PtrToStructure<NativeMethods.AceHeader>(acePointer);
                long aceOffset = acePointer.ToInt64() - dacl.ToInt64();
                if (aceHeader.AceSize < Marshal.SizeOf<NativeMethods.AceHeader>()
                    || (aceHeader.AceSize & 3) != 0
                    || aceOffset < Marshal.SizeOf<NativeMethods.AclHeader>()
                    || aceOffset + aceHeader.AceSize > size.AclBytesInUse)
                {
                    return Result<DirectoryAclSnapshot>.Failure(InvalidDaclError);
                }

                var aceBinary = new byte[aceHeader.AceSize];
                Marshal.Copy(acePointer, aceBinary, 0, aceBinary.Length);
                aceBinaries[(int)index] = aceBinary;
            }

            return Result<DirectoryAclSnapshot>.Success(new DirectoryAclSnapshot(
                new SecurityIdentifier(owner).Value,
                new SecurityIdentifier(group).Value,
                (ControlFlags)control,
                aclHeader.AclRevision,
                daclBinary,
                aceBinaries));
        }
        finally
        {
            if (securityDescriptor != nint.Zero)
            {
                NativeMethods.LocalFree(securityDescriptor);
            }
        }
    }

    public Result<DirectoryAclPreparation> PrepareDenyAce(
        SafeFileHandle handle,
        SecurityIdentifier sid)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(sid);

        Result<DirectoryAclSnapshot> beforeResult = ReadSnapshot(handle);
        if (beforeResult.IsFailure)
        {
            return Result<DirectoryAclPreparation>.Failure(beforeResult.Error!);
        }

        DirectoryAclSnapshot before = beforeResult.Value;
        byte[] targetAce = CreateTargetAce(sid);
        if (CountAce(before, targetAce) != 0)
        {
            return Result<DirectoryAclPreparation>.Failure(ExistingAceError);
        }

        return Result<DirectoryAclPreparation>.Success(new DirectoryAclPreparation(
            handle,
            before,
            targetAce,
            RecoveryAclEvidence.Prepared(before, targetAce)));
    }

    public Result<DirectoryAclOperation> ApplyPreparedDenyAce(
        SafeFileHandle handle,
        DirectoryAclPreparation preparation,
        out DirectoryAclOperation? operation)
    {
        operation = null;
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(preparation);
        if (!ReferenceEquals(handle, preparation.Handle))
        {
            return Result<DirectoryAclOperation>.Failure(VerificationError);
        }

        Result<DirectoryAclSnapshot> currentResult = ReadSnapshot(handle);
        if (currentResult.IsFailure
            || !SnapshotsEqual(preparation.BeforeSnapshot, currentResult.Value)
            || CountAce(currentResult.Value, preparation.AceBinary) != 0)
        {
            return Result<DirectoryAclOperation>.Failure(VerificationError);
        }

        var updatedAcl = new RawAcl(preparation.BeforeSnapshot.DaclBinary, 0);
        int insertIndex = FindDenyInsertIndex(updatedAcl);
        updatedAcl.InsertAce(insertIndex, GenericAce.CreateFromBinaryForm(preparation.AceBinary, 0));
        Result setResult = SetDacl(handle, preparation.BeforeSnapshot, updatedAcl);
        if (setResult.IsFailure)
        {
            return Result<DirectoryAclOperation>.Failure(setResult.Error!);
        }

        Result<DirectoryAclSnapshot> afterResult = ReadSnapshot(handle);
        operation = new DirectoryAclOperation(
            handle,
            preparation.BeforeSnapshot,
            preparation.AceBinary,
            preparation.Evidence);
        if (afterResult.IsFailure)
        {
            return Result<DirectoryAclOperation>.Failure(VerificationError);
        }

        if (!IsSingleAddition(
                preparation.BeforeSnapshot,
                afterResult.Value,
                preparation.AceBinary))
        {
            return Result<DirectoryAclOperation>.Failure(VerificationError);
        }

        byte[] actualAce = afterResult.Value.AceBinaries.Single(
            ace => ace.AsSpan().SequenceEqual(preparation.AceBinary));
        operation = operation with
        {
            Evidence = preparation.Evidence.Applied(afterResult.Value, actualAce),
        };

        if (_testHook?.FailAddPostValidation == true)
        {
            Result rollback = RemoveProvenAce(
                handle,
                operation,
                afterResult.Value,
                isRollback: true);
            if (rollback.IsSuccess)
            {
                operation = null;
                return Result<DirectoryAclOperation>.Failure(RolledBackError);
            }

            return Result<DirectoryAclOperation>.Failure(VerificationError);
        }

        return Result<DirectoryAclOperation>.Success(operation);
    }

    public Result<DirectoryAclOperation> AddDenyAce(
        SafeFileHandle handle,
        SecurityIdentifier sid,
        out DirectoryAclOperation? operation)
    {
        operation = null;
        Result<DirectoryAclPreparation> preparation = PrepareDenyAce(handle, sid);
        return preparation.IsSuccess
            ? ApplyPreparedDenyAce(handle, preparation.Value, out operation)
            : Result<DirectoryAclOperation>.Failure(preparation.Error!);
    }

    public Result RemoveDenyAce(
        SafeFileHandle handle,
        DirectoryAclOperation operation)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(operation);
        if (!ReferenceEquals(handle, operation.Handle))
        {
            return Result.Failure(VerificationError);
        }

        if (!OperationEvidenceMatches(operation))
        {
            return Result.Failure(VerificationError);
        }

        Result<DirectoryAclSnapshot> currentResult = ReadSnapshot(handle);
        if (currentResult.IsFailure)
        {
            return Result.Failure(currentResult.Error!);
        }

        int matchCount = CountAce(currentResult.Value, operation.AceBinary);
        if (matchCount == 0)
        {
            return string.Equals(
                    RecoveryAclEvidence.ComputeDaclDigest(currentResult.Value),
                    operation.Evidence.BaselineDaclSha256,
                    StringComparison.Ordinal)
                ? Result.Success()
                : Result.Failure(VerificationError);
        }

        if (matchCount != 1
            || !string.Equals(
                RecoveryAclEvidence.ComputeDaclDigest(currentResult.Value),
                operation.Evidence.PostApplyDaclSha256,
                StringComparison.Ordinal)
            || !IsSingleAddition(
                operation.BeforeSnapshot,
                currentResult.Value,
                operation.AceBinary))
        {
            return Result.Failure(VerificationError);
        }

        return RemoveProvenAce(handle, operation, currentResult.Value, isRollback: false);
    }

    private Result RemoveProvenAce(
        SafeFileHandle handle,
        DirectoryAclOperation operation,
        DirectoryAclSnapshot current,
        bool isRollback)
    {
        var currentAcl = new RawAcl(current.DaclBinary, 0);
        int matchingIndex = Enumerable.Range(0, currentAcl.Count)
            .Single(index => AceEquals(currentAcl[index], operation.AceBinary));
        currentAcl.RemoveAce(matchingIndex);
        Result setResult = SetDacl(handle, operation.BeforeSnapshot, currentAcl, isRollback);
        if (setResult.IsFailure)
        {
            return isRollback ? Result.Failure(VerificationError) : setResult;
        }

        Result<DirectoryAclSnapshot> afterResult = ReadSnapshot(handle);
        return afterResult.IsSuccess
            && CountAce(afterResult.Value, operation.AceBinary) == 0
            && string.Equals(
                RecoveryAclEvidence.ComputeDaclDigest(afterResult.Value),
                operation.Evidence.BaselineDaclSha256,
                StringComparison.Ordinal)
            ? Result.Success()
            : Result.Failure(VerificationError);
    }

    private static bool OperationEvidenceMatches(DirectoryAclOperation operation)
    {
        if (operation.Evidence.PostApplyDaclSha256 is null
            || !IsSupportedTargetAce(operation.AceBinary))
        {
            return false;
        }

        try
        {
            return string.Equals(
                    RecoveryAclEvidence.ComputeAceFingerprint(operation.AceBinary),
                    operation.Evidence.AceFingerprintSha256,
                    StringComparison.Ordinal)
                && string.Equals(
                    RecoveryAclEvidence.ComputeDaclDigest(operation.BeforeSnapshot),
                    operation.Evidence.BaselineDaclSha256,
                    StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsSupportedTargetAce(byte[] aceBinary)
    {
        ArgumentNullException.ThrowIfNull(aceBinary);
        try
        {
            return GenericAce.CreateFromBinaryForm(aceBinary, 0) is CommonAce ace
                && ace.AceQualifier == AceQualifier.AccessDenied
                && ace.AccessMask == (int)FolderDenyAccessMask.Value
                && ace.AceFlags == (AceFlags.ContainerInherit | AceFlags.ObjectInherit)
                && !ace.IsInherited
                && !ace.IsCallback;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static CommonAce CreateCommonAce(SecurityIdentifier sid) =>
        new(
            AceFlags.ContainerInherit | AceFlags.ObjectInherit,
            AceQualifier.AccessDenied,
            (int)FolderDenyAccessMask.Value,
            sid,
            isCallback: false,
            opaque: null);

    internal static byte[] CreateTargetAce(SecurityIdentifier sid)
    {
        CommonAce ace = CreateCommonAce(sid);
        var binary = new byte[ace.BinaryLength];
        ace.GetBinaryForm(binary, 0);
        return binary;
    }

    internal static DirectoryAclSnapshot CreateBaselineSnapshot(
        DirectoryAclSnapshot current,
        byte[] targetAce)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(targetAce);
        var acl = new RawAcl(current.DaclBinary, 0);
        int matchingIndex = Enumerable.Range(0, acl.Count)
            .Single(index => AceEquals(acl[index], targetAce));
        acl.RemoveAce(matchingIndex);
        var binary = new byte[acl.BinaryLength];
        acl.GetBinaryForm(binary, 0);
        return new DirectoryAclSnapshot(
            current.OwnerSid,
            current.GroupSid,
            current.ControlFlags,
            current.AclRevision,
            binary,
            current.AceBinaries
                .Where(ace => !ace.AsSpan().SequenceEqual(targetAce))
                .ToArray());
    }

    private static int FindDenyInsertIndex(RawAcl acl)
    {
        int index = 0;
        while (index < acl.Count
               && acl[index] is QualifiedAce qualified
               && qualified.AceQualifier == AceQualifier.AccessDenied
               && (qualified.AceFlags & AceFlags.Inherited) == 0)
        {
            index++;
        }

        return index;
    }

    private static Result SetDacl(
        SafeFileHandle handle,
        DirectoryAclSnapshot snapshot,
        RawAcl acl) =>
        SetDaclCore(handle, snapshot, acl);

    private Result SetDacl(
        SafeFileHandle handle,
        DirectoryAclSnapshot snapshot,
        RawAcl acl,
        bool isRollback)
    {
        if (isRollback && _testHook?.FailRollbackWrite == true)
        {
            return Result.Failure(VerificationError);
        }

        return SetDaclCore(handle, snapshot, acl);
    }

    private static Result SetDaclCore(
        SafeFileHandle handle,
        DirectoryAclSnapshot snapshot,
        RawAcl acl)
    {
        var binary = new byte[acl.BinaryLength];
        acl.GetBinaryForm(binary, 0);
        nint buffer = Marshal.AllocHGlobal(binary.Length);
        try
        {
            Marshal.Copy(binary, 0, buffer, binary.Length);
            uint securityInformation = NativeMethods.DaclSecurityInformation
                | (snapshot.IsProtected
                    ? NativeMethods.ProtectedDaclSecurityInformation
                    : NativeMethods.UnprotectedDaclSecurityInformation);
            uint error = NativeMethods.SetSecurityInfo(
                handle,
                NativeMethods.SeObjectType.FileObject,
                securityInformation,
                nint.Zero,
                nint.Zero,
                buffer,
                nint.Zero);
            return error == NativeMethods.ErrorSuccess
                ? Result.Success()
                : NativeFailure(error);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool IsSingleAddition(
        DirectoryAclSnapshot before,
        DirectoryAclSnapshot after,
        byte[] targetAce)
    {
        if (before.ControlFlags != after.ControlFlags
            || before.AclRevision != after.AclRevision
            || before.OwnerSid != after.OwnerSid
            || before.GroupSid != after.GroupSid
            || CountAce(after, targetAce) != 1
            || after.AceBinaries.Count != before.AceBinaries.Count + 1)
        {
            return false;
        }

        int beforeIndex = 0;
        foreach (byte[] ace in after.AceBinaries)
        {
            if (ace.AsSpan().SequenceEqual(targetAce))
            {
                continue;
            }

            if (beforeIndex >= before.AceBinaries.Count
                || !ace.AsSpan().SequenceEqual(before.AceBinaries[beforeIndex]))
            {
                return false;
            }

            beforeIndex++;
        }

        return beforeIndex == before.AceBinaries.Count;
    }

    internal static bool SnapshotsEqual(DirectoryAclSnapshot expected, DirectoryAclSnapshot actual) =>
        expected.ControlFlags == actual.ControlFlags
        && expected.AclRevision == actual.AclRevision
        && expected.OwnerSid == actual.OwnerSid
        && expected.GroupSid == actual.GroupSid
        && expected.AceBinaries.Count == actual.AceBinaries.Count
        && expected.AceBinaries.Zip(actual.AceBinaries)
            .All(pair => pair.First.AsSpan().SequenceEqual(pair.Second));

    internal static bool HasPresentNonNullDacl(ControlFlags controlFlags, nint dacl) =>
        (controlFlags & ControlFlags.DiscretionaryAclPresent) != 0 && dacl != nint.Zero;

    private static int CountAce(DirectoryAclSnapshot snapshot, byte[] ace) =>
        snapshot.AceCounts.TryGetValue(Convert.ToHexString(ace), out int count) ? count : 0;

    private static bool AceEquals(GenericAce ace, byte[] expected)
    {
        if (ace.BinaryLength != expected.Length)
        {
            return false;
        }

        var binary = new byte[ace.BinaryLength];
        ace.GetBinaryForm(binary, 0);
        return binary.AsSpan().SequenceEqual(expected);
    }

    private static Result NativeFailure(uint error) =>
        Result.Failure(new Error(
            "windows.acl.native_call_failed",
            $"SetSecurityInfo failed with Windows error {error}.",
            ErrorCategory.PlatformError));

    private static Result<T> NativeFailure<T>(string operation, uint error) =>
        Result<T>.Failure(new Error(
            "windows.acl.native_call_failed",
            $"{operation} failed with Windows error {error}.",
            ErrorCategory.PlatformError));
}

public sealed record DirectoryAclOperation(
    SafeFileHandle Handle,
    DirectoryAclSnapshot BeforeSnapshot,
    byte[] AceBinary,
    RecoveryAclEvidence Evidence);

public sealed record DirectoryAclPreparation(
    SafeFileHandle Handle,
    DirectoryAclSnapshot BeforeSnapshot,
    byte[] AceBinary,
    RecoveryAclEvidence Evidence);

internal interface IDirectoryAclEditorTestHook
{
    bool FailAddPostValidation { get; }

    bool FailRollbackWrite { get; }
}
