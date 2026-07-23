using FolderSessionLock.Broker.Security;

namespace FolderSessionLock.Broker.Recovery;

internal sealed class RecoveryTaskRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RecoveryRecord> _byTaskId = [];
    private readonly Dictionary<Guid, Guid> _taskByRecordId = [];
    private readonly Dictionary<Guid, Guid> _taskByRequestId = [];
    private readonly Dictionary<Guid, Guid> _recordByRequestId = [];

    internal bool BeginRequest(Guid requestId, Guid taskId)
    {
        lock (_gate)
        {
            if (_taskByRequestId.TryGetValue(requestId, out Guid existingTaskId))
            {
                return existingTaskId == taskId;
            }

            _taskByRequestId.Add(requestId, taskId);
            if (_byTaskId.TryGetValue(taskId, out RecoveryRecord? record))
            {
                _recordByRequestId[requestId] = record.RecordId;
            }

            return true;
        }
    }

    internal bool TryAdd(RecoveryRecord record)
    {
        lock (_gate)
        {
            if (_byTaskId.ContainsKey(record.TaskId) || _taskByRecordId.ContainsKey(record.RecordId))
            {
                return false;
            }

            _byTaskId.Add(record.TaskId, record);
            _taskByRecordId.Add(record.RecordId, record.TaskId);
            foreach (KeyValuePair<Guid, Guid> request in _taskByRequestId)
            {
                if (request.Value == record.TaskId)
                {
                    _recordByRequestId[request.Key] = record.RecordId;
                }
            }
            return true;
        }
    }

    internal RecoveryRecord? GetByRecordId(Guid recordId)
    {
        lock (_gate)
        {
            return _taskByRecordId.TryGetValue(recordId, out Guid taskId)
                ? _byTaskId.GetValueOrDefault(taskId)
                : null;
        }
    }

    internal RecoveryRecord? GetByTaskId(Guid taskId)
    {
        lock (_gate)
        {
            return _byTaskId.GetValueOrDefault(taskId);
        }
    }

    internal bool Update(RecoveryRecord record)
    {
        lock (_gate)
        {
            if (!_taskByRecordId.TryGetValue(record.RecordId, out Guid taskId)
                || taskId != record.TaskId
                || !_byTaskId.ContainsKey(taskId))
            {
                return false;
            }

            _byTaskId[taskId] = record;
            return true;
        }
    }

    internal void Remove(Guid recordId)
    {
        lock (_gate)
        {
            if (_taskByRecordId.Remove(recordId, out Guid taskId))
            {
                _byTaskId.Remove(taskId);
            }

            foreach (Guid requestId in _recordByRequestId
                         .Where(pair => pair.Value == recordId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _recordByRequestId.Remove(requestId);
            }
        }
    }

    internal ReplaySideEffectEvidence InspectRequest(Guid requestId)
    {
        lock (_gate)
        {
            if (!_taskByRequestId.ContainsKey(requestId))
            {
                return ReplaySideEffectEvidence.Unknown;
            }

            return _recordByRequestId.ContainsKey(requestId)
                ? ReplaySideEffectEvidence.RecoveryRecordPresent
                : ReplaySideEffectEvidence.None;
        }
    }
}

internal sealed class RecoveryReplaySideEffectEvidenceProvider(
    RecoveryTaskRegistry registry) : IReplaySideEffectEvidenceProvider
{
    public ReplaySideEffectEvidence Inspect(Guid requestId) => registry.InspectRequest(requestId);
}
