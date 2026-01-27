// ============================================================================
// PROJECT: ChokaQ
// DESCRIPTION: Pillar-aware In-Memory Implementation of IJobStorage.
// ============================================================================

using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Storage;

/// <summary>
/// Refactored In-Memory Storage (Phase 2.1). 
/// Emulates Three-Pillar architecture using separate ConcurrentDictionaries.
/// </summary>
public class InMemoryJobStorage : IJobStorage
{
    // --- The Three Pillars (Memory) ---
    private readonly ConcurrentDictionary<string, JobEntity> _activeJobs = new();
    private readonly ConcurrentDictionary<string, JobSucceededEntity> _archive = new();
    private readonly ConcurrentDictionary<string, JobMorgueEntity> _morgue = new();

    // --- Control Plane & Stats ---
    private readonly ConcurrentDictionary<string, QueueEntity> _queues = new();
    private readonly ConcurrentDictionary<string, StatsSummaryEntity> _stats = new();

    // ========================================================================
    // 1. CORE OPERATIONS
    // ========================================================================

    public ValueTask<string> EnqueueAsync(string queue, string jobType, string payload, int priority = 10, string? createdBy = null, string? tags = null, TimeSpan? delay = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        var job = new JobEntity(
            Id: id,
            Queue: queue,
            Type: jobType,
            Payload: payload,
            Tags: tags,
            IdempotencyKey: idempotencyKey,
            Priority: priority,
            Status: JobStatus.Pending,
            AttemptCount: 0,
            WorkerId: null,
            HeartbeatUtc: null,
            ScheduledAtUtc: delay.HasValue ? now.Add(delay.Value) : null,
            CreatedAtUtc: now,
            LastUpdatedUtc: now,
            CreatedBy: createdBy,
            LastModifiedBy: null
        );

        _activeJobs[id] = job;

        // Ensure queue stats exist
        UpdateStats(queue, s => s with { LastActivityUtc = now });

        return ValueTask.FromResult(id);
    }

    public ValueTask<IEnumerable<JobEntity>> FetchNextBatchAsync(string workerId, int limit, string[]? allowedQueues, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var available = _activeJobs.Values
            .Where(j => j.Status == JobStatus.Pending)
            .Where(j => allowedQueues == null || allowedQueues.Contains(j.Queue))
            .Where(j => j.ScheduledAtUtc == null || j.ScheduledAtUtc <= now)
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.CreatedAtUtc)
            .Take(limit)
            .ToList();

        var locked = new List<JobEntity>();
        foreach (var job in available)
        {
            var updatedJob = job with
            {
                Status = JobStatus.Fetched,
                WorkerId = workerId,
                HeartbeatUtc = now,
                LastUpdatedUtc = now
            };

            if (_activeJobs.TryUpdate(job.Id, updatedJob, job))
            {
                locked.Add(updatedJob);
            }
        }

        return ValueTask.FromResult<IEnumerable<JobEntity>>(locked);
    }

    public ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            _activeJobs.TryUpdate(jobId, job with { HeartbeatUtc = DateTime.UtcNow }, job);
        }
        return ValueTask.CompletedTask;
    }

    // ========================================================================
    // 2. TRANSITIONS (Logic Parity)
    // ========================================================================

    public Task RetryJobAsync(string jobId, int nextAttempt, TimeSpan delay, string? lastError, CancellationToken ct = default)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            // Mutation in Hot storage: increment attempts, set delay, release worker lock
            var retriedJob = job with
            {
                AttemptCount = nextAttempt,
                ScheduledAtUtc = DateTime.UtcNow.Add(delay),
                WorkerId = null,            // Release back to the pool
                HeartbeatUtc = null,        // Reset heartbeat
                Status = JobStatus.Pending, // Make visible for picking again
                LastUpdatedUtc = DateTime.UtcNow
                // Note: Transient errors are not stored in JobEntity to keep it lightweight.
                // Only the final error is stored in Morgue.
            };

            _activeJobs.TryUpdate(jobId, retriedJob, job);
            UpdateStats(job.Queue, s => s with { RetriedTotal = s.RetriedTotal + 1 });
        }
        return Task.CompletedTask;
    }

    public Task ArchiveAsSuccessAsync(JobSucceededEntity archiveRecord, CancellationToken ct = default)
    {
        // Atomic Move: Remove from Hot, Add to Archive
        if (_activeJobs.TryRemove(archiveRecord.Id, out _))
        {
            _archive[archiveRecord.Id] = archiveRecord;
            UpdateStats(archiveRecord.Queue, s => s with { SucceededTotal = s.SucceededTotal + 1, LastActivityUtc = DateTime.UtcNow });
        }
        return Task.CompletedTask;
    }

    public Task ArchiveAsMorgueAsync(JobMorgueEntity morgueRecord, CancellationToken ct = default)
    {
        // Atomic Move: Remove from Hot, Add to Morgue
        if (_activeJobs.TryRemove(morgueRecord.Id, out _))
        {
            _morgue[morgueRecord.Id] = morgueRecord;
            UpdateStats(morgueRecord.Queue, s => s with { FailedTotal = s.FailedTotal + 1, LastActivityUtc = DateTime.UtcNow });
        }
        return Task.CompletedTask;
    }

    public Task ResurrectJobAsync(string jobId, CancellationToken ct = default)
    {
        if (_morgue.TryRemove(jobId, out var deadJob))
        {
            var resurrected = new JobEntity(
                deadJob.Id, deadJob.Queue, deadJob.Type, deadJob.Payload, deadJob.Tags, null, 10,
                JobStatus.Pending, 0, null, null, null, DateTime.UtcNow, DateTime.UtcNow, deadJob.CreatedBy, deadJob.LastModifiedBy
            );
            _activeJobs[jobId] = resurrected;
        }
        return Task.CompletedTask;
    }

    // ========================================================================
    // 3. DIVINE MODE (Safety Gates)
    // ========================================================================

    public Task UpdateJobDataAsync(JobDataUpdateDto update, CancellationToken ct = default)
    {
        if (!_activeJobs.TryGetValue(update.JobId, out var job))
            throw new InvalidOperationException("Job not found in Hot storage.");

        // Safety Gate: Only Pending jobs can be edited
        if (job.Status != JobStatus.Pending)
            throw new InvalidOperationException($"Cannot edit job in status {job.Status}.");

        var updated = job with
        {
            Payload = update.NewPayload ?? job.Payload,
            Tags = update.NewTags ?? job.Tags,
            Priority = update.NewPriority ?? job.Priority,
            LastModifiedBy = update.UpdatedBy,
            LastUpdatedUtc = DateTime.UtcNow
        };

        _activeJobs.TryUpdate(update.JobId, updated, job);
        return Task.CompletedTask;
    }

    public Task PurgeJobAsync(string id, CancellationToken ct = default)
    {
        _activeJobs.TryRemove(id, out _);
        _archive.TryRemove(id, out _);
        _morgue.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    // ========================================================================
    // 4. OBSERVABILITY
    // ========================================================================

    public ValueTask<StatsSummaryEntity?> GetSummaryStatsAsync(string queue, CancellationToken ct = default)
    {
        _stats.TryGetValue(queue, out var stats);
        return ValueTask.FromResult(stats);
    }

    public ValueTask<IEnumerable<JobSucceededEntity>> GetHistoryJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
        => ValueTask.FromResult(_archive.Values.Where(x => x.Queue == queue).Skip(skip).Take(take));

    public ValueTask<IEnumerable<JobMorgueEntity>> GetMorgueJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
        => ValueTask.FromResult(_morgue.Values.Where(x => x.Queue == queue).Skip(skip).Take(take));

    public ValueTask<JobEntity?> GetJobEntityAsync(string id, CancellationToken ct = default)
        => ValueTask.FromResult(_activeJobs.TryGetValue(id, out var j) ? j : null);

    public ValueTask<JobSucceededEntity?> GetSucceededEntityAsync(string id, CancellationToken ct = default)
        => ValueTask.FromResult(_archive.TryGetValue(id, out var j) ? j : null);

    public ValueTask<JobMorgueEntity?> GetMorgueEntityAsync(string id, CancellationToken ct = default)
        => ValueTask.FromResult(_morgue.TryGetValue(id, out var j) ? j : null);

    public ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IEnumerable<QueueEntity>>(_queues.Values);

    public ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueEntity(queueName, isPaused, true, DateTime.UtcNow, 300),
            (_, old) => old with { IsPaused = isPaused, LastUpdatedUtc = DateTime.UtcNow });
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> MarkZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default)
    {
        var timeout = DateTime.UtcNow.AddSeconds(-globalTimeoutSeconds);
        var zombies = _activeJobs.Values
            .Where(j => j.Status != JobStatus.Pending && j.HeartbeatUtc < timeout)
            .ToList();

        foreach (var z in zombies)
        {
            _activeJobs.TryUpdate(z.Id, z with { Status = JobStatus.Pending, WorkerId = null, HeartbeatUtc = null }, z);
        }
        return ValueTask.FromResult(zombies.Count);
    }

    public ValueTask UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default) => ValueTask.CompletedTask;

    private void UpdateStats(string queue, Func<StatsSummaryEntity, StatsSummaryEntity> updateAction)
    {
        _stats.AddOrUpdate(queue,
            _ => updateAction(new StatsSummaryEntity(queue, 0, 0, 0, DateTime.UtcNow)),
            (_, old) => updateAction(old));
    }
}