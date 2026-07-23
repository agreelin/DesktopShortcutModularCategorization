using System.Security.AccessControl;
using System.Security.Principal;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Models;
using FolderSessionLock.Windows.Security;
using FolderSessionLock.Windows.Services;
using FolderSessionLock.Windows.Tests.Infrastructure;

namespace FolderSessionLock.Windows.Tests.Security;

public sealed class DirectoryAclEditorTests
{
    [Fact]
    public async Task PrepareDenyAce_CreatesEvidenceWithoutWritingAcl()
    {
        AclTestSafetyGate.EnsureCanWrite();
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string targetPath = Path.Combine(directory.Path, "target");
        Directory.CreateDirectory(targetPath);
        using ValidatedDirectory target = ValidateTarget(directory.Path, targetPath);
        SecurityIdentifier logonSid = await GetLogonSid();
        var editor = new DirectoryAclEditor();
        DirectoryAclSnapshot before = editor.ReadSnapshot(target.Handle).Value;

        Result<DirectoryAclPreparation> preparation = editor.PrepareDenyAce(
            target.Handle,
            logonSid);
        DirectoryAclSnapshot after = editor.ReadSnapshot(target.Handle).Value;

        Assert.True(preparation.IsSuccess, preparation.Error?.Message);
        Assert.True(DirectoryAclEditor.SnapshotsEqual(before, after));
        Assert.Null(preparation.Value.Evidence.PostApplyDaclSha256);
        Assert.Equal(
            RecoveryAclEvidence.ComputeDaclDigest(before),
            preparation.Value.Evidence.BaselineDaclSha256);
    }

    [Fact]
    public async Task AddAndRemove_PreservesOriginalAclAndUsesSameHandle()
    {
        AclTestSafetyGate.EnsureCanWrite();
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string targetPath = Path.Combine(directory.Path, "target");
        Directory.CreateDirectory(targetPath);
        using ValidatedDirectory target = ValidateTarget(directory.Path, targetPath);
        SecurityIdentifier logonSid = await GetLogonSid();
        var editor = new DirectoryAclEditor();
        DirectoryAclOperation? operation = null;

        try
        {
            Result<DirectoryAclSnapshot> beforeResult = editor.ReadSnapshot(target.Handle);
            Assert.True(beforeResult.IsSuccess, beforeResult.Error?.Message);

            Result<DirectoryAclOperation> addResult = editor.AddDenyAce(
                target.Handle,
                logonSid,
                out DirectoryAclOperation? addOperation);
            Assert.True(addResult.IsSuccess, addResult.Error?.Message);
            operation = Assert.IsType<DirectoryAclOperation>(addOperation);
            Assert.Same(addResult.Value, operation);
            Assert.Same(target.Handle, operation.Handle);

            Result<DirectoryAclSnapshot> afterResult = editor.ReadSnapshot(target.Handle);
            Assert.True(afterResult.IsSuccess, afterResult.Error?.Message);
            Assert.Equal(beforeResult.Value.ControlFlags, afterResult.Value.ControlFlags);
            Assert.Equal(beforeResult.Value.IsProtected, afterResult.Value.IsProtected);
            Assert.Equal(beforeResult.Value.IsAutoInherited, afterResult.Value.IsAutoInherited);
            Assert.Equal(beforeResult.Value.AceBinaries.Count + 1, afterResult.Value.AceBinaries.Count);
            Assert.Equal(1, CountAce(afterResult.Value, operation.AceBinary));

            GenericAce parsed = GenericAce.CreateFromBinaryForm(operation.AceBinary, 0);
            CommonAce ace = Assert.IsType<CommonAce>(parsed);
            Assert.Equal(AceQualifier.AccessDenied, ace.AceQualifier);
            Assert.Equal((int)FolderDenyAccessMask.Value, ace.AccessMask);
            Assert.Equal(logonSid, ace.SecurityIdentifier);
            Assert.Equal(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                ace.AceFlags);
            Assert.False(ace.IsInherited);
        }
        finally
        {
            if (operation is not null)
            {
                Result removeResult = editor.RemoveDenyAce(target.Handle, operation);
                if (removeResult.IsFailure)
                {
                    var failure = new InvalidOperationException(removeResult.Error!.Message);
                    AclTestSafetyGate.Block(failure);
                    throw failure;
                }
            }
        }

        Result<DirectoryAclSnapshot> restoredResult = editor.ReadSnapshot(target.Handle);
        Assert.True(restoredResult.IsSuccess, restoredResult.Error?.Message);
        AssertSnapshotsEqual(operation!.BeforeSnapshot, restoredResult.Value);
        Assert.True(editor.RemoveDenyAce(target.Handle, operation!).IsSuccess);
        string probe = Path.Combine(targetPath, "probe.txt");
        File.WriteAllText(probe, "probe");
        Assert.Equal("probe", File.ReadAllText(probe));
        File.Delete(probe);
    }

    [Fact]
    public async Task RemoveDenyAce_RejectsOtherAceDriftBeforeWriting()
    {
        AclTestSafetyGate.EnsureCanWrite();
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string targetPath = Path.Combine(directory.Path, "target");
        Directory.CreateDirectory(targetPath);
        using ValidatedDirectory target = ValidateTarget(directory.Path, targetPath);
        SecurityIdentifier logonSid = await GetLogonSid();
        var editor = new DirectoryAclEditor();
        DirectoryAclOperation? first = null;
        DirectoryAclOperation? second = null;

        try
        {
            Result<DirectoryAclOperation> firstResult = editor.AddDenyAce(
                target.Handle,
                logonSid,
                out first);
            Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);
            first = Assert.IsType<DirectoryAclOperation>(first);
            Result<DirectoryAclOperation> secondResult = editor.AddDenyAce(
                target.Handle,
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                out DirectoryAclOperation? secondOperation);
            Assert.True(secondResult.IsSuccess, secondResult.Error?.Message);
            second = Assert.IsType<DirectoryAclOperation>(secondOperation);
            Assert.Same(second, secondOperation);
            Result<DirectoryAclSnapshot> drifted = editor.ReadSnapshot(target.Handle);

            Result remove = editor.RemoveDenyAce(target.Handle, first);

            Assert.True(remove.IsFailure);
            Assert.Equal(ErrorCategory.UnrecoverableError, remove.Error!.Category);
            Result<DirectoryAclSnapshot> unchanged = editor.ReadSnapshot(target.Handle);
            Assert.True(unchanged.IsSuccess);
            AssertSnapshotsEqual(drifted.Value, unchanged.Value);
        }
        finally
        {
            try
            {
                if (second is not null)
                {
                    Assert.True(editor.RemoveDenyAce(target.Handle, second).IsSuccess);
                }

                if (first is not null)
                {
                    Assert.True(editor.RemoveDenyAce(target.Handle, first).IsSuccess);
                }
            }
            catch (Exception exception)
            {
                AclTestSafetyGate.Block(exception);
                throw;
            }
        }
    }

    [Fact]
    public async Task AddDenyAce_PostValidationFailureRollsBackWhenStateIsProven()
    {
        AclTestSafetyGate.EnsureCanWrite();
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string targetPath = Path.Combine(directory.Path, "target");
        Directory.CreateDirectory(targetPath);
        using ValidatedDirectory target = ValidateTarget(directory.Path, targetPath);
        SecurityIdentifier logonSid = await GetLogonSid();
        var baselineEditor = new DirectoryAclEditor();
        DirectoryAclSnapshot before = baselineEditor.ReadSnapshot(target.Handle).Value;
        var editor = new DirectoryAclEditor(new EditorTestHook(true, false));

        Result<DirectoryAclOperation> result = editor.AddDenyAce(
            target.Handle,
            logonSid,
            out DirectoryAclOperation? operation);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.PlatformError, result.Error!.Category);
        Assert.Null(operation);
        AssertSnapshotsEqual(before, baselineEditor.ReadSnapshot(target.Handle).Value);
    }

    [Fact]
    public async Task AddDenyAce_RollbackFailureReturnsUnrecoverableAndRetainsOperation()
    {
        AclTestSafetyGate.EnsureCanWrite();
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string targetPath = Path.Combine(directory.Path, "target");
        Directory.CreateDirectory(targetPath);
        using ValidatedDirectory target = ValidateTarget(directory.Path, targetPath);
        SecurityIdentifier logonSid = await GetLogonSid();
        var editor = new DirectoryAclEditor(new EditorTestHook(true, true));
        DirectoryAclOperation? operation = null;

        try
        {
            Result<DirectoryAclOperation> result = editor.AddDenyAce(
                target.Handle,
                logonSid,
                out operation);

            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCategory.UnrecoverableError, result.Error!.Category);
            Assert.NotNull(operation);
        }
        finally
        {
            if (operation is not null)
            {
                Result cleanup = new DirectoryAclEditor().RemoveDenyAce(
                    target.Handle,
                    operation);
                if (cleanup.IsFailure)
                {
                    var failure = new InvalidOperationException(cleanup.Error!.Message);
                    AclTestSafetyGate.Block(failure);
                    throw failure;
                }
            }
        }
    }

    [Fact]
    public async Task RemoveDenyAce_RejectsEvidenceAndTupleMismatchBeforeWriting()
    {
        AclTestSafetyGate.EnsureCanWrite();
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string targetPath = Path.Combine(directory.Path, "target");
        Directory.CreateDirectory(targetPath);
        using ValidatedDirectory target = ValidateTarget(directory.Path, targetPath);
        SecurityIdentifier logonSid = await GetLogonSid();
        var editor = new DirectoryAclEditor();
        DirectoryAclOperation? operation = null;

        try
        {
            Result<DirectoryAclOperation> add = editor.AddDenyAce(
                target.Handle,
                logonSid,
                out operation);
            Assert.True(add.IsSuccess, add.Error?.Message);
            operation = Assert.IsType<DirectoryAclOperation>(operation);
            byte[] allowAce = CreateAce(logonSid, AceQualifier.AccessAllowed);
            DirectoryAclOperation[] invalidOperations =
            [
                operation with
                {
                    Evidence = operation.Evidence with
                    {
                        AceFingerprintSha256 = new string('a', 64),
                    },
                },
                operation with
                {
                    Evidence = operation.Evidence with
                    {
                        BaselineDaclSha256 = new string('a', 64),
                    },
                },
                operation with
                {
                    Evidence = operation.Evidence with
                    {
                        PostApplyDaclSha256 = new string('a', 64),
                    },
                },
                operation with
                {
                    AceBinary = allowAce,
                    Evidence = operation.Evidence with
                    {
                        AceFingerprintSha256 = RecoveryAclEvidence.ComputeAceFingerprint(allowAce),
                    },
                },
            ];

            foreach (DirectoryAclOperation invalidOperation in invalidOperations)
            {
                DirectoryAclSnapshot before = editor.ReadSnapshot(target.Handle).Value;

                Result remove = editor.RemoveDenyAce(target.Handle, invalidOperation);

                Assert.True(remove.IsFailure);
                Assert.Equal(ErrorCategory.UnrecoverableError, remove.Error!.Category);
                AssertSnapshotsEqual(before, editor.ReadSnapshot(target.Handle).Value);
            }
        }
        finally
        {
            if (operation is not null)
            {
                Result cleanup = editor.RemoveDenyAce(target.Handle, operation);
                if (cleanup.IsFailure)
                {
                    var failure = new InvalidOperationException(cleanup.Error!.Message);
                    AclTestSafetyGate.Block(failure);
                    throw failure;
                }
            }
        }
    }

    [Fact]
    public async Task AddDenyAce_InterleavedOperationsRemainBoundToTheirOwnCalls()
    {
        AclTestSafetyGate.EnsureCanWrite();
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string firstPath = Path.Combine(directory.Path, "first");
        string secondPath = Path.Combine(directory.Path, "second");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        using ValidatedDirectory firstTarget = ValidateTarget(directory.Path, firstPath);
        using ValidatedDirectory secondTarget = ValidateTarget(directory.Path, secondPath);
        SecurityIdentifier logonSid = await GetLogonSid();
        var hook = new MutableEditorTestHook
        {
            FailAddPostValidation = true,
            FailRollbackWrite = true,
        };
        var editor = new DirectoryAclEditor(hook);
        DirectoryAclOperation? firstOperation = null;
        DirectoryAclOperation? secondOperation = null;

        try
        {
            Result<DirectoryAclOperation> first = editor.AddDenyAce(
                firstTarget.Handle,
                logonSid,
                out firstOperation);
            hook.FailAddPostValidation = false;
            hook.FailRollbackWrite = false;
            Result<DirectoryAclOperation> second = editor.AddDenyAce(
                secondTarget.Handle,
                logonSid,
                out secondOperation);

            Assert.True(first.IsFailure);
            Assert.NotNull(firstOperation);
            Assert.Same(firstTarget.Handle, firstOperation.Handle);
            Assert.True(second.IsSuccess, second.Error?.Message);
            Assert.NotNull(secondOperation);
            Assert.Same(secondTarget.Handle, secondOperation.Handle);
            Assert.NotSame(firstOperation, secondOperation);
        }
        finally
        {
            try
            {
                if (secondOperation is not null)
                {
                    Assert.True(editor.RemoveDenyAce(secondTarget.Handle, secondOperation).IsSuccess);
                }

                if (firstOperation is not null)
                {
                    Assert.True(editor.RemoveDenyAce(firstTarget.Handle, firstOperation).IsSuccess);
                }
            }
            catch (Exception exception)
            {
                AclTestSafetyGate.Block(exception);
                throw;
            }
        }
    }

    [Fact]
    public void IsSingleAddition_RequiresExactlyOneTargetAce()
    {
        byte[] originalAce = [1, 2, 3];
        byte[] targetAce = [4, 5, 6];
        var before = new DirectoryAclSnapshot(
            "S-1-5-18",
            "S-1-5-18",
            ControlFlags.DiscretionaryAclPresent,
            2,
            [0],
            [originalAce]);
        var zero = new DirectoryAclSnapshot(
            before.OwnerSid,
            before.GroupSid,
            before.ControlFlags,
            before.AclRevision,
            [0],
            [originalAce]);
        var one = new DirectoryAclSnapshot(
            before.OwnerSid,
            before.GroupSid,
            before.ControlFlags,
            before.AclRevision,
            [0],
            [targetAce, originalAce]);
        var two = new DirectoryAclSnapshot(
            before.OwnerSid,
            before.GroupSid,
            before.ControlFlags,
            before.AclRevision,
            [0],
            [targetAce, targetAce, originalAce]);

        Assert.False(DirectoryAclEditor.IsSingleAddition(before, zero, targetAce));
        Assert.True(DirectoryAclEditor.IsSingleAddition(before, one, targetAce));
        Assert.False(DirectoryAclEditor.IsSingleAddition(before, two, targetAce));
    }

    [Fact]
    public async Task AddDenyAce_RejectsPreexistingIdenticalAce()
    {
        AclTestSafetyGate.EnsureCanWrite();
        using TemporaryTestDirectory directory = TemporaryTestDirectory.Create();
        string targetPath = Path.Combine(directory.Path, "target");
        Directory.CreateDirectory(targetPath);
        using ValidatedDirectory target = ValidateTarget(directory.Path, targetPath);
        SecurityIdentifier logonSid = await GetLogonSid();
        var editor = new DirectoryAclEditor();
        DirectoryAclOperation? operation = null;

        try
        {
            Result<DirectoryAclOperation> first = editor.AddDenyAce(
                target.Handle,
                logonSid,
                out DirectoryAclOperation? firstOperation);
            Assert.True(first.IsSuccess, first.Error?.Message);
            operation = firstOperation;

            Result<DirectoryAclOperation> duplicate = editor.AddDenyAce(
                target.Handle,
                logonSid,
                out DirectoryAclOperation? duplicateOperation);

            Assert.True(duplicate.IsFailure);
            Assert.Null(duplicateOperation);
            Assert.Equal("windows.acl.identical_ace_exists", duplicate.Error!.Code);
        }
        finally
        {
            if (operation is not null)
            {
                Result removeResult = editor.RemoveDenyAce(target.Handle, operation);
                if (removeResult.IsFailure)
                {
                    var failure = new InvalidOperationException(removeResult.Error!.Message);
                    AclTestSafetyGate.Block(failure);
                    throw failure;
                }
            }
        }
    }

    private static async Task<SecurityIdentifier> GetLogonSid()
    {
        Result<SessionIdentity> identity = await new WindowsSessionIdentityProvider().GetCurrentAsync();
        Assert.True(identity.IsSuccess, identity.Error?.Message);
        return new SecurityIdentifier(identity.Value.LogonSid);
    }

    private static ValidatedDirectory ValidateTarget(string temporaryRoot, string targetPath)
    {
        string policyRoot = Path.Combine(temporaryRoot, "Policy");
        var roots = new SystemPathRoots(
            Path.Combine(policyRoot, "User"),
            Path.Combine(policyRoot, "Desktop"),
            Path.Combine(policyRoot, "Documents"),
            Path.Combine(policyRoot, "Downloads"),
            Path.Combine(policyRoot, "Windows"),
            Path.Combine(policyRoot, "System"),
            [Path.Combine(policyRoot, "ProgramFiles")],
            Path.Combine(policyRoot, "ProgramData"));
        var policy = new FolderPathSafetyPolicy(
            Path.Combine(policyRoot, "Repository"),
            Path.Combine(policyRoot, "Installation"),
            [Path.Combine(policyRoot, "Synchronization")],
            roots);
        Result<ValidatedDirectory> result = new WindowsFolderPathValidator(policy).Validate(targetPath);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private static int CountAce(DirectoryAclSnapshot snapshot, byte[] ace) =>
        snapshot.AceCounts.TryGetValue(Convert.ToHexString(ace), out int count) ? count : 0;

    private static byte[] CreateAce(SecurityIdentifier sid, AceQualifier qualifier)
    {
        var ace = new CommonAce(
            AceFlags.ContainerInherit | AceFlags.ObjectInherit,
            qualifier,
            (int)FolderDenyAccessMask.Value,
            sid,
            isCallback: false,
            opaque: null);
        var binary = new byte[ace.BinaryLength];
        ace.GetBinaryForm(binary, 0);
        return binary;
    }

    private static void AssertSnapshotsEqual(
        DirectoryAclSnapshot expected,
        DirectoryAclSnapshot actual)
    {
        Assert.Equal(expected.ControlFlags, actual.ControlFlags);
        Assert.Equal(expected.OwnerSid, actual.OwnerSid);
        Assert.Equal(expected.GroupSid, actual.GroupSid);
        Assert.Equal(expected.AceBinaries.Count, actual.AceBinaries.Count);
        for (int index = 0; index < expected.AceBinaries.Count; index++)
        {
            Assert.Equal(expected.AceBinaries[index], actual.AceBinaries[index]);
        }
    }

    private sealed record EditorTestHook(
        bool FailAddPostValidation,
        bool FailRollbackWrite) : IDirectoryAclEditorTestHook;

    private sealed class MutableEditorTestHook : IDirectoryAclEditorTestHook
    {
        public bool FailAddPostValidation { get; set; }

        public bool FailRollbackWrite { get; set; }
    }
}
