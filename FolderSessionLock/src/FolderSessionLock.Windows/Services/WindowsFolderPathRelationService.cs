using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Windows.Services;

public sealed class WindowsFolderPathRelationService : IFolderPathRelationService
{
    public FolderPathRelation GetRelation(FolderPath existingPath, FolderPath requestedPath)
    {
        string existingRoot = Path.GetPathRoot(existingPath.Value)!;
        string requestedRoot = Path.GetPathRoot(requestedPath.Value)!;
        if (!string.Equals(existingRoot, requestedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return FolderPathRelation.Unrelated;
        }

        string[] existingComponents = SplitComponents(existingPath.Value, existingRoot);
        string[] requestedComponents = SplitComponents(requestedPath.Value, requestedRoot);
        int commonLength = Math.Min(existingComponents.Length, requestedComponents.Length);
        for (int index = 0; index < commonLength; index++)
        {
            if (!string.Equals(
                    existingComponents[index],
                    requestedComponents[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                return FolderPathRelation.Unrelated;
            }
        }

        if (existingComponents.Length == requestedComponents.Length)
        {
            return FolderPathRelation.Same;
        }

        return existingComponents.Length < requestedComponents.Length
            ? FolderPathRelation.Ancestor
            : FolderPathRelation.Descendant;
    }

    private static string[] SplitComponents(string path, string root) =>
        path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
}
