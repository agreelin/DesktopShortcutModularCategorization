namespace FolderSessionLock.Core.Results;

public sealed record Error
{
    public Error(string code, string message, ErrorCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        Category = category;
    }

    public string Code { get; }

    public string Message { get; }

    public ErrorCategory Category { get; }
}
