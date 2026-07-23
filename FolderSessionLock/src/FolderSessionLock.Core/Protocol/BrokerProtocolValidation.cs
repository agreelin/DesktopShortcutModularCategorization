namespace FolderSessionLock.Protocol;

internal static class BrokerProtocolValidation
{
    private const string ErrorPrefix = "FSL_E_";

    public static bool IsErrorCode(string? value)
    {
        if (value is null || !value.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> suffix = value.AsSpan(ErrorPrefix.Length);
        if (suffix.IsEmpty || suffix[0] == '_' || suffix[^1] == '_')
        {
            return false;
        }

        bool previousWasUnderscore = false;
        foreach (char character in suffix)
        {
            if (character == '_')
            {
                if (previousWasUnderscore)
                {
                    return false;
                }

                previousWasUnderscore = true;
                continue;
            }

            if (character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9'))
            {
                return false;
            }

            previousWasUnderscore = false;
        }

        return true;
    }
}
