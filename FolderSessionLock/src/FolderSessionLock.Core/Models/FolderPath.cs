using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Models;

public readonly record struct FolderPath
{
    private FolderPath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public bool IsValid => !string.IsNullOrEmpty(Value);

    public static Result<FolderPath> Create(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<FolderPath>.Failure(new Error(
                "folder_path.empty",
                "A folder path is required.",
                ErrorCategory.ValidationFailed));
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return Result<FolderPath>.Failure(new Error(
                "folder_path.relative",
                "A folder path must be fully qualified.",
                ErrorCategory.ValidationFailed));
        }

        try
        {
            string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return Result<FolderPath>.Success(new FolderPath(normalizedPath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<FolderPath>.Failure(new Error(
                "folder_path.invalid",
                "The folder path is not a valid fully qualified path.",
                ErrorCategory.ValidationFailed));
        }
    }

    public override string ToString() => Value ?? string.Empty;
}
