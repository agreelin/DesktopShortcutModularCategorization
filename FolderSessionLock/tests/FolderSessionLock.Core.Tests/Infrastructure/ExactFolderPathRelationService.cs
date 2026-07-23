using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;

namespace FolderSessionLock.Core.Tests.Infrastructure;

internal sealed class ExactFolderPathRelationService : IFolderPathRelationService
{
    public FolderPathRelation GetRelation(FolderPath existingPath, FolderPath requestedPath) =>
        existingPath == requestedPath ? FolderPathRelation.Same : FolderPathRelation.Unrelated;
}
