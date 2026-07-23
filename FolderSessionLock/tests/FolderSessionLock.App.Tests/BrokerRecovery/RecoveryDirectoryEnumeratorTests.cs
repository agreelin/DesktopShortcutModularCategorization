using FolderSessionLock.Protocol;

namespace FolderSessionLock.Broker.Recovery.Tests;

public sealed class RecoveryDirectoryEnumeratorTests
{
    [Fact]
    public async Task Enumerate_ClassifiesCanonicalAuxiliaryOrphanedAndInvalidArtifacts()
    {
        string root = CreateRoot();
        try
        {
            Guid canonicalId = Guid.Parse("11111111-2222-4333-8444-555555555555");
            Guid orphanId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
            File.WriteAllBytes(Path.Combine(root, $"{canonicalId:D}.fslr"), []);
            File.WriteAllBytes(Path.Combine(root, $"{canonicalId:D}.bak"), []);
            File.WriteAllBytes(Path.Combine(root, $"{canonicalId:D}.tmp-{Guid.NewGuid():D}"), []);
            File.WriteAllBytes(Path.Combine(root, $"{orphanId:D}.bak"), []);
            File.WriteAllBytes(Path.Combine(root, "NOT-A-GUID.fslr"), []);
            Directory.CreateDirectory(Path.Combine(root, "child"));

            RecoveryDirectoryEnumerator enumerator = RecoveryTestData.CreateEnumerator(root);
            var result = await enumerator.EnumerateAsync();

            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Single(result.Value.CanonicalRecords);
            Assert.Equal(canonicalId, result.Value.CanonicalRecords[0].RecordId);
            Assert.Equal(2, result.Value.AuxiliaryArtifactCount);
            Assert.Equal(3, result.Value.InvalidArtifactCount);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_ARTIFACT_INVALID, result.Value.PrimaryErrorCode);
            Assert.True(File.Exists(Path.Combine(root, $"{orphanId:D}.bak")));
            Assert.True(File.Exists(Path.Combine(root, "NOT-A-GUID.fslr")));
            Assert.True(Directory.Exists(Path.Combine(root, "child")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Enumerate_RejectsThe1025thCanonicalRecordBeforeCleanup()
    {
        string root = CreateRoot();
        try
        {
            for (int index = 1; index <= 1025; index++)
            {
                File.WriteAllBytes(Path.Combine(root, $"{new Guid(index, 0, 0, new byte[8]):D}.fslr"), []);
            }

            var result = await RecoveryTestData.CreateEnumerator(root).EnumerateAsync();

            Assert.True(result.IsFailure);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED, result.Error!.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Enumerate_RejectsThe4097thTopLevelEntry()
    {
        string root = CreateRoot();
        try
        {
            for (int index = 0; index <= 4096; index++)
            {
                File.WriteAllBytes(Path.Combine(root, $"artifact-{index:D4}"), []);
            }

            var result = await RecoveryTestData.CreateEnumerator(root).EnumerateAsync();

            Assert.True(result.IsFailure);
            Assert.Equal(BrokerErrorCodes.FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED, result.Error!.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FolderSessionLock.Tests",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        return root;
    }
}
