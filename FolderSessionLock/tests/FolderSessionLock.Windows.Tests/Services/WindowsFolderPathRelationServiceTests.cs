using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;
using FolderSessionLock.Windows.Services;

namespace FolderSessionLock.Windows.Tests.Services;

public sealed class WindowsFolderPathRelationServiceTests
{
    private readonly WindowsFolderPathRelationService _service = new();

    [Fact]
    public void GetRelation_EquivalentComponentsAreSame()
    {
        FolderPath existing = CreatePath(@"C:\Root\One");
        FolderPath requested = CreatePath(@"c:\root\one\");

        FolderPathRelation relation = _service.GetRelation(existing, requested);

        Assert.Equal(FolderPathRelation.Same, relation);
    }

    [Fact]
    public void GetRelation_ExistingParentIsAncestor()
    {
        FolderPath existing = CreatePath(@"C:\Root\One");
        FolderPath requested = CreatePath(@"C:\Root\One\Child");

        FolderPathRelation relation = _service.GetRelation(existing, requested);

        Assert.Equal(FolderPathRelation.Ancestor, relation);
    }

    [Fact]
    public void GetRelation_ExistingChildIsDescendant()
    {
        FolderPath existing = CreatePath(@"C:\Root\One\Child");
        FolderPath requested = CreatePath(@"C:\Root\One");

        FolderPathRelation relation = _service.GetRelation(existing, requested);

        Assert.Equal(FolderPathRelation.Descendant, relation);
    }

    [Fact]
    public void GetRelation_AdjacentNamesAreUnrelated()
    {
        FolderPath existing = CreatePath(@"C:\Root\One");
        FolderPath requested = CreatePath(@"C:\Root\OneTwo");

        FolderPathRelation relation = _service.GetRelation(existing, requested);

        Assert.Equal(FolderPathRelation.Unrelated, relation);
    }

    private static FolderPath CreatePath(string value)
    {
        Result<FolderPath> result = FolderPath.Create(value);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }
}
