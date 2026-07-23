using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Core.Tests.Models;

public sealed class ValueObjectTests
{
    private static readonly LockDurationPolicy DurationPolicy =
        LockDurationPolicy.Create(TimeSpan.FromMinutes(1), TimeSpan.FromHours(8)).Value;

    [Fact]
    public void FolderLockTaskId_Create_PreservesValue()
    {
        Guid value = Guid.NewGuid();

        var result = FolderLockTaskId.Create(value);

        Assert.True(result.IsSuccess);
        Assert.Equal(value, result.Value.Value);
        Assert.Equal(result.Value, FolderLockTaskId.Create(value).Value);
    }

    [Fact]
    public void FolderLockTaskId_DefaultAndEmpty_AreInvalid()
    {
        Assert.False(default(FolderLockTaskId).IsValid);
        Assert.True(FolderLockTaskId.Create(Guid.Empty).IsFailure);
    }

    [Fact]
    public void FolderPath_Create_NormalizesTrailingSeparatorWithoutFileSystemAccess()
    {
        string path = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "FolderSessionLock-Value");

        var result = FolderPath.Create(path + Path.DirectorySeparatorChar);

        Assert.True(result.IsSuccess);
        Assert.Equal(Path.GetFullPath(path), result.Value.Value);
        Assert.Equal(result.Value, FolderPath.Create(path).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative-folder")]
    public void FolderPath_Create_RejectsInvalidInput(string path)
    {
        Assert.True(FolderPath.Create(path).IsFailure);
    }

    [Fact]
    public void LockDurationPolicy_Create_RejectsInvalidBounds()
    {
        Assert.True(LockDurationPolicy.Create(TimeSpan.Zero, TimeSpan.FromMinutes(1)).IsFailure);
        Assert.True(LockDurationPolicy.Create(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)).IsFailure);
    }

    [Fact]
    public void LockDuration_Create_AcceptsInclusiveBounds()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), LockDuration.Create(TimeSpan.FromMinutes(1), DurationPolicy).Value.Value);
        Assert.Equal(TimeSpan.FromHours(8), LockDuration.Create(TimeSpan.FromHours(8), DurationPolicy).Value.Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(481)]
    public void LockDuration_Create_RejectsValuesOutsidePolicy(int totalMinutes)
    {
        Assert.True(LockDuration.Create(TimeSpan.FromMinutes(totalMinutes), DurationPolicy).IsFailure);
    }

    [Fact]
    public void LockDuration_Create_RejectsPositiveValueBelowMinimum()
    {
        Assert.True(LockDuration.Create(TimeSpan.FromSeconds(30), DurationPolicy).IsFailure);
    }
}
