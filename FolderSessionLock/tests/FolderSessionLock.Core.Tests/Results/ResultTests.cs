using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Tests.Results;

public sealed class ResultTests
{
    [Fact]
    public void Success_HasNoError()
    {
        Result result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(ErrorCategory.ValidationFailed)]
    [InlineData(ErrorCategory.InsufficientPermissions)]
    [InlineData(ErrorCategory.UnsupportedPath)]
    [InlineData(ErrorCategory.PlatformError)]
    [InlineData(ErrorCategory.RecoverableError)]
    [InlineData(ErrorCategory.UnrecoverableError)]
    public void Failure_PreservesErrorCategory(ErrorCategory category)
    {
        var error = new Error("test.error", "Test error.", category);

        Result result = Result.Failure(error);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
        Assert.Equal(category, result.Error!.Category);
    }

    [Fact]
    public void GenericSuccess_ExposesValue()
    {
        Result<int> result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void GenericFailure_RejectsValueAccess()
    {
        var error = new Error(
            "test.failure",
            "Test failure.",
            ErrorCategory.RecoverableError);
        Result<int> result = Result<int>.Failure(error);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => result.Value);

        Assert.Equal("A failed result does not contain a value.", exception.Message);
        Assert.Same(error, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Error_RejectsBlankCode(string code)
    {
        Assert.Throws<ArgumentException>(
            () => new Error(code, "Message.", ErrorCategory.ValidationFailed));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Error_RejectsBlankMessage(string message)
    {
        Assert.Throws<ArgumentException>(
            () => new Error("test.error", message, ErrorCategory.ValidationFailed));
    }
}
