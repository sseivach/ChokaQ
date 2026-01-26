using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// Thread-safe in-memory implementation compatible with "Three Pillars".
/// Supports atomic batch fetching and simulated archiving.
/// </summary>
public class InMemoryJobStorage : IJobStorage
{
    private readonly int _maxCapacity;
    // Single dict simulating all 3 tables (Hot/Archive/Morgue) via Status filtering
    private readonly ConcurrentDictionary<string, JobStorageDto> _jobs = new();
    private readonly ConcurrentDictionary<string, QueueInfo> _queues = new();

    public InMemoryJobStorage(InMemoryStorageOptions options)
    {
        _maxCapacity = options.MaxCapacity;
        _queues.TryAdd("default", new QueueInfo("default"));
    }

    // --- 1. Production Flow ---

    public ValueTask CreateJobAsync(JobStorageDto job, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(job.IdempotencyKey))
        {
            if (_jobs.Values.Any(j => j.IdempotencyKey == job.IdempotencyKey))
                return ValueTask.CompletedTask;
        }

        EnsureCapacity();

        var jobToSave = job; // Assuming DTO is clean or needs defaults
        if (jobToSave.CreatedAtUtc == default) jobToSave.CreatedAtUtc = DateTime.UtcNow;
        if (string.IsNullOrEmpty(jobToSave.Queue)) jobToSave.Queue = "default";

        // Ensure initial status is Pending
        jobToSave.Status = JobStatus.Pending;

        _jobs[job.Id] = jobToSave;
        _queues.TryAdd(jobToSave.Queue, new QueueInfo(jobToSave.Queue));

        return ValueTask.CompletedTask;
    }

    public ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(string workerId, int limit, string[]? allowedQueues, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var result = new List<JobStorageDto>();

        // Lock globally for simulation of atomic batch update
        lock (_jobs)
        {
            var candidates = _jobs.Values
                .Where(j => j.Status == JobStatus.Pending
                            && (allowedQueues == null || allowedQueues.Contains(j.Queue))
                            && (!j.ScheduledAtUtc.HasValue || j.ScheduledAtUtc <= now))
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.ScheduledAtUtc)
                .ThenBy(j => j.CreatedAtUtc)
                .Take(limit)
                .ToList();

            foreach (var job in candidates)
            {
                var locked = new JobStorageDto
                {
                    Id = job.Id,
                    Queue = job.Queue,
                    Type = job.Type,
                    Payload = job.Payload,
                    Priority = job.Priority,
                    Tags = job.Tags,
                    IdempotencyKey = job.IdempotencyKey,
                    CreatedAtUtc = job.CreatedAtUtc,
                    ScheduledAtUtc = job.ScheduledAtUtc,

                    Status = JobStatus.Processing, // Immediate transition
                    WorkerId = workerId,
                    FetchedAtUtc = now,
                    StartedAtUtc = now,
                    HeartbeatUtc = now,
                    LastUpdatedUtc = now,
                    AttemptCount = job.AttemptCount + 1
                };

                _jobs[job.Id] = locked;
                result.Add(locked);
            }
        }
        return new ValueTask<IEnumerable<JobStorageDto>>(result);
    }

    public ValueTask UpdateHeartbeatAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.HeartbeatUtc = DateTime.UtcNow;
            job.LastUpdatedUtc = DateTime.UtcNow;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ArchiveJobAsync(string jobId, JobStatus finalStatus, string? error = null, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            double? duration = null;
            if (finalStatus == JobStatus.Succeeded && job.FetchedAtUtc.HasValue)
                duration = (DateTime.UtcNow - job.FetchedAtUtc.Value).TotalMilliseconds;

            var archived = new JobStorageDto
            {
                Id = job.Id,
                Queue = job.Queue,
                Type = job.Type,
                Payload = job.Payload,
                Priority = job.Priority,
                Tags = job.Tags,
                IdempotencyKey = job.IdempotencyKey,
                CreatedAtUtc = job.CreatedAtUtc,
                ScheduledAtUtc = job.ScheduledAtUtc,
                FetchedAtUtc = job.FetchedAtUtc,
                StartedAtUtc = job.StartedAtUtc,
                AttemptCount = job.AttemptCount,

                Status = finalStatus,
                FinishedAtUtc = finalStatus == JobStatus.Succeeded ? DateTime.UtcNow : null,
                FailedAtUtc = finalStatus == JobStatus.Failed ? DateTime.UtcNow : null,
                ErrorDetails = error,
                DurationMs = duration,
                WorkerId = null,
                LastUpdatedUtc = DateTime.UtcNow
            };
            _jobs[jobId] = archived;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ScheduleRetryAsync(string jobId, DateTime nextAttemptUtc, int attemptCount, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var retry = new JobStorageDto
            {
                Id = job.Id,
                Queue = job.Queue,
                Type = job.Type,
                Payload = job.Payload,
                Priority = job.Priority,
                Tags = job.Tags,
                IdempotencyKey = job.IdempotencyKey,
                CreatedAtUtc = job.CreatedAtUtc,

                Status = JobStatus.Pending,
                ScheduledAtUtc = nextAttemptUtc,
                WorkerId = null,
                AttemptCount = attemptCount,
                HeartbeatUtc = null,
                LastUpdatedUtc = DateTime.UtcNow
            };
            _jobs[jobId] = retry;
        }
        return ValueTask.CompletedTask;
    }

    // --- 2. Resilience ---

    public ValueTask<int> RescueZombiesAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        int count = 0;
        foreach (var job in _jobs.Values)
        {
            if (job.Status == JobStatus.Processing)
            {
                int effectiveTimeout = (int)timeout.TotalSeconds;
                if (_queues.TryGetValue(job.Queue, out var q) && q.ZombieTimeoutSeconds.HasValue)
                    effectiveTimeout = q.ZombieTimeoutSeconds.Value;

                if (job.HeartbeatUtc.HasValue && job.HeartbeatUtc < now.AddSeconds(-effectiveTimeout))
                {
                    job.Status = JobStatus.Pending;
                    job.WorkerId = null;
                    job.ErrorDetails = "Zombie Rescued";
                    job.HeartbeatUtc = null;
                    job.LastUpdatedUtc = now;
                    count++;
                }
            }
        }
        return new ValueTask<int>(count);
    }

    public ValueTask ResurrectJobAsync(string jobId, string? newPayload = null, string? newTags = null, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status != JobStatus.Failed) return ValueTask.CompletedTask;

            var resurrected = new JobStorageDto
            {
                Id = job.Id,
                Queue = job.Queue,
                Type = job.Type,
                Priority = job.Priority,
                IdempotencyKey = job.IdempotencyKey,
                CreatedAtUtc = DateTime.UtcNow,

                Status = JobStatus.Pending,
                Payload = newPayload ?? job.Payload,
                Tags = newTags ?? job.Tags,
                AttemptCount = 0,
                ErrorDetails = null,
                WorkerId = null,
                LastUpdatedUtc = DateTime.UtcNow
            };
            _jobs[jobId] = resurrected;
        }
        return ValueTask.CompletedTask;
    }

    // --- 3. Read Models ---

    public ValueTask<JobStorageDto?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        _jobs.TryGetValue(jobId, out var job);
        return new ValueTask<JobStorageDto?>(job);
    }

    public ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(JobStatus status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _jobs.Values.Where(j => j.Status == status);

        var result = query
            .OrderByDescending(j => j.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return new ValueTask<IEnumerable<JobStorageDto>>(result);
    }

    public ValueTask<IEnumerable<QueueStatsDto>> GetSummaryStatsAsync(CancellationToken ct = default)
    {
        var stats = _jobs.Values
            .GroupBy(j => j.Queue)
            .Select(g => new QueueStatsDto
            {
                QueueName = g.Key,
                SucceededTotal = g.Count(x => x.Status == JobStatus.Succeeded),
                FailedTotal = g.Count(x => x.Status == JobStatus.Failed),
                LastUpdatedUtc = DateTime.UtcNow
            })
            .ToList();

        foreach (var q in _queues.Values)
        {
            if (!stats.Any(s => s.QueueName == q.Name))
                stats.Add(new QueueStatsDto { QueueName = q.Name, LastUpdatedUtc = DateTime.UtcNow });
        }
        return new ValueTask<IEnumerable<QueueStatsDto>>(stats);
    }

    public ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default)
    {
        var stats = _jobs.Values
            .GroupBy(j => j.Queue)
            .Select(g => new QueueDto(
                Name: g.Key,
                IsPaused: _queues.TryGetValue(g.Key, out var q) && q.IsPaused,
                PendingCount: g.Count(j => j.Status == JobStatus.Pending),
                ProcessingCount: g.Count(j => j.Status == JobStatus.Processing),
                FetchedCount: 0, SucceededCount: 0, FailedCount: 0, CancelledCount: 0, // Not used in new UI
                ZombieTimeoutSeconds: _queues.TryGetValue(g.Key, out var qi) ? qi.ZombieTimeoutSeconds : null,
                FirstJobAtUtc: g.Min(j => j.CreatedAtUtc),
                LastJobAtUtc: g.Max(j => j.CreatedAtUtc)
            ))
            .ToList();

        foreach (var q in _queues.Values)
        {
            if (!stats.Any(s => s.Name == q.Name))
                stats.Add(new QueueDto(q.Name, q.IsPaused, 0, 0, 0, 0, 0, 0, q.ZombieTimeoutSeconds, null, null));
        }

        return new ValueTask<IEnumerable<QueueDto>>(stats.OrderBy(x => x.Name));
    }

    // --- 4. Admin Ops ---

    public ValueTask UpdatePayloadAsync(string jobId, string payload, string? tags = null, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status != JobStatus.Pending) throw new InvalidOperationException("Not Pending");
            job.Payload = payload;
            if (tags != null) job.Tags = tags;
            job.LastUpdatedUtc = DateTime.UtcNow;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateJobPriorityAsync(string id, int newPriority, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(id, out var job)) job.Priority = newPriority;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueInfo(queueName) { IsPaused = isPaused },
            (k, v) => { v.IsPaused = isPaused; return v; });
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueInfo(queueName) { ZombieTimeoutSeconds = timeoutSeconds },
            (k, v) => { v.ZombieTimeoutSeconds = timeoutSeconds; return v; });
        return ValueTask.CompletedTask;
    }

    // Helpers
    private void EnsureCapacity()
    {
        if (_jobs.Count >= _maxCapacity)
        {
            var victims = _jobs.Values
                .Where(j => j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed)
                .OrderBy(j => j.CreatedAtUtc)
                .Take(100)
                .Select(j => j.Id)
                .ToList();
            foreach (var id in victims) _jobs.TryRemove(id, out _);
        }
    }

    private class QueueInfo
    {
        public string Name { get; }
        public bool IsPaused { get; set; }
        public int? ZombieTimeoutSeconds { get; set; }
        public QueueInfo(string name) => Name = name;
    }
}