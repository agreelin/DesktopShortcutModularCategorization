using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Core.Abstractions;

public interface IFolderPathRelationService
{
    FolderPathRelation GetRelation(FolderPath existingPath, FolderPath requestedPath);
}
