using System.Collections.Concurrent;
using FolderSessionLock.Core.Abstractions;
using FolderSessionLock.Core.Models;
using FolderSessionLock.Core.Results;

namespace FolderSessionLock.Core.Tests.Infrastructure;

internal sealed class RecordingFolderLockService : IFolderLockService
{
    private int _createCallCount;
    private int _removeCallCount;

    public ConcurrentQueue<FolderLockRequest> CreateRequests { get; } = new();

    public ConcurrentQueue<(Guid TaskId, LockRemovalIntent Intent)> RemoveRequests { get; } = new();

    public int CreateCallCount => Volatile.Read(ref _createCallCount);

    public int RemoveCallCount => Volatile.Read(ref _removeCallCount);

    public Func<FolderLockRequest, Result<Guid>>? CreateHandler { get; init; }

    public Func<FolderLockRequest, Task<Result<Guid>>>? AsyncCreateHandler { get; init; }

    public Func<Guid, LockRemovalIntent, Result>? RemoveHandler { get; init; }

    public Func<Guid, LockRemovalIntent, Task<Result>>? AsyncRemoveHandler { get; init; }

    public async ValueTask<Result<Guid>> CreateLockAsync(
        FolderLockRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _createCallCount);
        CreateRequests.Enqueue(request);
        if (AsyncCreateHandler is not null)
        {
            return await AsyncCreateHandler(request);
        }

        return CreateHandler?.Invoke(request) ?? Result<Guid>.Success(request.TaskId);
    }

    public async ValueTask<Result> RemoveLockAsync(
        Guid taskId,
        LockRemovalIntent intent,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _removeCallCount);
        RemoveRequests.Enqueue((taskId, intent));
        if (AsyncRemoveHandler is not null)
        {
            return await AsyncRemoveHandler(taskId, intent);
        }

        return RemoveHandler?.Invoke(taskId, intent) ?? Result.Success();
    }
}
