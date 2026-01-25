using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// Thread-safe in-memory implementation of the job storage.
/// Suitable for "Pipe Mode" (Rocket Speed) or local development.
/// </summary>
public class InMemoryJobStorage : IJobStorage
{
    private readonly int _maxCapacity;
    private readonly ConcurrentDictionary<string, JobStorageDto> _jobs = new();
    private readonly ConcurrentDictionary<string, QueueInfo> _queues = new();

    public InMemoryJobStorage(InMemoryStorageOptions options)
    {
        _maxCapacity = options.MaxCapacity;
        // Initialize default queue to ensure it always exists
        _queues.TryAdd("default", new QueueInfo("default"));
    }

    /// <inheritdoc />
    public ValueTask<string> CreateJobAsync(
        string id,
        string queue,
        string jobType,
        string payload,
        int priority = 10,
        string? createdBy = null,
        string? tags = null,
        TimeSpan? delay = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        // 1. Check Idempotency (Simple check for In-Memory)
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = _jobs.Values.FirstOrDefault(j => j.IdempotencyKey == idempotencyKey);
            if (existing != null)
            {
                return new ValueTask<string>(existing.Id);
            }
        }

        var jobDto = new JobStorageDto(
            Id: id,
            Queue: queue,
            Type: jobType,
            Payload: payload,
            Status: JobStatus.Pending,
            AttemptCount: 0,
            Priority: priority,
            ScheduledAtUtc: delay.HasValue ? DateTime.UtcNow.Add(delay.Value) : null,
            Tags: tags,
            IdempotencyKey: idempotencyKey,
            WorkerId: null,
            ErrorDetails: null,
            CreatedBy: createdBy,
            CreatedAtUtc: DateTime.UtcNow,
            StartedAtUtc: null,
            FinishedAtUtc: null,
            LastUpdatedUtc: DateTime.UtcNow
        );

        SaveJobInternal(jobDto);
        return new ValueTask<string>(id);
    }

    /// <inheritdoc />
    public ValueTask<JobStorageDto?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        _jobs.TryGetValue(jobId, out var job);
        return new ValueTask<JobStorageDto?>(job);
    }

    /// <inheritdoc />
    public ValueTask<bool> UpdateJobStateAsync(
        string jobId,
        JobStatus status,
        string? errorDetails = null,
        CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var updatedJob = job with
            {
                Status = status,
                ErrorDetails = errorDetails,
                StartedAtUtc = status == JobStatus.Processing ? DateTime.UtcNow : job.StartedAtUtc,
                FinishedAtUtc = (status == JobStatus.Succeeded || status == JobStatus.Failed || status == JobStatus.Cancelled) ? DateTime.UtcNow : job.FinishedAtUtc,
                LastUpdatedUtc = DateTime.UtcNow
            };
            _jobs[jobId] = updatedJob;
            return new ValueTask<bool>(true);
        }
        return new ValueTask<bool>(false);
    }

    /// <inheritdoc />
    public Task MarkAsProcessingAsync(string jobId, CancellationToken ct)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            // Transition from Fetched (1) -> Processing (2)
            // And set the real Start Time
            var updatedJob = job with
            {
                Status = JobStatus.Processing,
                StartedAtUtc = DateTime.UtcNow,
                LastUpdatedUtc = DateTime.UtcNow
            };
            _jobs[jobId] = updatedJob;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default)
    {
        // Light update to confirm worker is alive
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var updated = job with { LastUpdatedUtc = DateTime.UtcNow };
            _jobs[jobId] = updated;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask UpdateJobPriorityAsync(string jobId, int priority, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var updatedJob = job with { Priority = priority, LastUpdatedUtc = DateTime.UtcNow };
            _jobs[jobId] = updatedJob;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> IncrementJobAttemptAsync(string jobId, int attemptCount, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var updatedJob = job with { AttemptCount = attemptCount, LastUpdatedUtc = DateTime.UtcNow };
            _jobs[jobId] = updatedJob;
            return new ValueTask<bool>(true);
        }
        return new ValueTask<bool>(false);
    }

    /// <inheritdoc />
    public ValueTask RescheduleJobAsync(
        string jobId,
        DateTime scheduledAtUtc,
        int attemptCount,
        string errorDetails,
        CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var updatedJob = job with
            {
                Status = JobStatus.Pending,
                ScheduledAtUtc = scheduledAtUtc,
                AttemptCount = attemptCount,
                ErrorDetails = errorDetails,
                WorkerId = null, // Release worker
                LastUpdatedUtc = DateTime.UtcNow
            };
            _jobs[jobId] = updatedJob;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(string workerId, int limit, string[]? allowedQueues, CancellationToken ct = default)
    {
        // 1. Identify jobs ready to be processed
        var batch = _jobs.Values
            .Where(j => j.Status == JobStatus.Pending &&
                        (allowedQueues == null || allowedQueues.Contains(j.Queue)) &&
                        (!j.ScheduledAtUtc.HasValue || j.ScheduledAtUtc <= DateTime.UtcNow))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAtUtc)
            .Take(limit)
            .ToList();

        var lockedJobs = new List<JobStorageDto>();

        foreach (var job in batch)
        {
            // 2. Lock them (Pending -> Fetched)
            var fetchedJob = job with
            {
                Status = JobStatus.Fetched,
                WorkerId = workerId,
                LastUpdatedUtc = DateTime.UtcNow,
                AttemptCount = job.AttemptCount + 1
            };

            // Optimistic concurrency check (TryUpdate)
            if (_jobs.TryUpdate(job.Id, fetchedJob, job))
            {
                lockedJobs.Add(fetchedJob);
            }
        }
        return new ValueTask<IEnumerable<JobStorageDto>>(lockedJobs);
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(int limit, CancellationToken ct = default)
    {
        var result = _jobs.Values.OrderByDescending(j => j.CreatedAtUtc).Take(limit);
        return new ValueTask<IEnumerable<JobStorageDto>>(result);
    }

    /// <inheritdoc />
    public ValueTask<JobCountsDto> GetJobCountsAsync(CancellationToken ct = default)
    {
        var values = _jobs.Values;
        var counts = new JobCountsDto(
            Pending: values.Count(x => x.Status == JobStatus.Pending),
            Fetched: values.Count(x => x.Status == JobStatus.Fetched),
            Processing: values.Count(x => x.Status == JobStatus.Processing),
            Succeeded: values.Count(x => x.Status == JobStatus.Succeeded),
            Failed: values.Count(x => x.Status == JobStatus.Failed),
            Cancelled: values.Count(x => x.Status == JobStatus.Cancelled),
            Total: values.Count
        );
        return new ValueTask<JobCountsDto>(counts);
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default)
    {
        var stats = _jobs.Values
            .GroupBy(j => j.Queue)
            .Select(g => new QueueDto(
                Name: g.Key,
                IsPaused: _queues.TryGetValue(g.Key, out var q) && q.IsPaused,
                PendingCount: g.Count(j => j.Status == JobStatus.Pending),
                FetchedCount: g.Count(j => j.Status == JobStatus.Fetched),
                ProcessingCount: g.Count(j => j.Status == JobStatus.Processing),
                FailedCount: g.Count(j => j.Status == JobStatus.Failed),
                SucceededCount: g.Count(j => j.Status == JobStatus.Succeeded),
                CancelledCount: g.Count(j => j.Status == JobStatus.Cancelled),
                ZombieTimeoutSeconds: _queues.TryGetValue(g.Key, out var qi) ? qi.ZombieTimeoutSeconds : null, // <--- Map Timeout
                FirstJobAtUtc: g.Min(j => j.StartedAtUtc),
                LastJobAtUtc: g.Max(j => j.FinishedAtUtc)
            ))
            .ToList();

        // Add empty queues from registry that have no jobs yet
        foreach (var q in _queues.Values)
        {
            if (!stats.Any(s => s.Name == q.Name))
                stats.Add(new QueueDto(q.Name, q.IsPaused, 0, 0, 0, 0, 0, 0, q.ZombieTimeoutSeconds, null, null));
        }

        // Sort: Oldest/Inactive first, Newest activity last
        var sortedStats = stats
            .OrderBy(x => x.LastJobAtUtc)
            .ThenBy(x => x.Name);

        return new ValueTask<IEnumerable<QueueDto>>(sortedStats);
    }

    /// <inheritdoc />
    public ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueInfo(queueName) { IsPaused = isPaused, LastUpdatedUtc = DateTime.UtcNow },
            (key, old) => { old.IsPaused = isPaused; old.LastUpdatedUtc = DateTime.UtcNow; return old; });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueInfo(queueName) { ZombieTimeoutSeconds = timeoutSeconds, LastUpdatedUtc = DateTime.UtcNow },
            (key, old) => { old.ZombieTimeoutSeconds = timeoutSeconds; old.LastUpdatedUtc = DateTime.UtcNow; return old; });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> MarkZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        int count = 0;

        foreach (var job in _jobs.Values)
        {
            // Only check Processing jobs
            if (job.Status == JobStatus.Processing)
            {
                // Determine effective timeout (Queue-specific OR Global)
                int timeout = globalTimeoutSeconds;
                if (_queues.TryGetValue(job.Queue, out var q) && q.ZombieTimeoutSeconds.HasValue)
                {
                    timeout = q.ZombieTimeoutSeconds.Value;
                }

                // Check heartbeat expiration
                if (job.LastUpdatedUtc < now.AddSeconds(-timeout))
                {
                    var zombie = job with
                    {
                        Status = JobStatus.Zombie,
                        ErrorDetails = "Zombie: Detected dead worker (In-Memory Heartbeat Expired)",
                        WorkerId = null, // Release worker lock
                        LastUpdatedUtc = now
                    };

                    // Optimistic update
                    if (_jobs.TryUpdate(job.Id, zombie, job))
                    {
                        count++;
                    }
                }
            }
        }
        return new ValueTask<int>(count);
    }

    private void SaveJobInternal(JobStorageDto job)
    {
        // Simple eviction mechanism to prevent OOM
        if (_jobs.Count >= _maxCapacity)
        {
            var jobsToRemove = _jobs.Values
                .Where(x => x.Status == JobStatus.Succeeded || x.Status == JobStatus.Failed || x.Status == JobStatus.Cancelled)
                .OrderBy(x => x.CreatedAtUtc)
                .Take(1000)
                .Select(x => x.Id)
                .ToList();

            foreach (var id in jobsToRemove) _jobs.TryRemove(id, out _);

            // Hard limit fallback
            if (_jobs.Count >= _maxCapacity)
            {
                var oldest = _jobs.Values
                    .OrderBy(x => x.CreatedAtUtc)
                    .Take(100)
                    .Select(x => x.Id);
                foreach (var id in oldest) _jobs.TryRemove(id, out _);
            }
        }

        _jobs[job.Id] = job;

        // Register queue if new
        if (!_queues.ContainsKey(job.Queue))
            _queues.TryAdd(job.Queue, new QueueInfo(job.Queue));
    }

    /// <summary>
    /// Internal DTO to track queue settings in memory.
    /// </summary>
    private class QueueInfo
    {
        public string Name { get; }
        public bool IsPaused { get; set; }
        public int? ZombieTimeoutSeconds { get; set; }
        public DateTime LastUpdatedUtc { get; set; }

        public QueueInfo(string name)
        {
            Name = name;
            LastUpdatedUtc = DateTime.UtcNow;
        }
    }
}