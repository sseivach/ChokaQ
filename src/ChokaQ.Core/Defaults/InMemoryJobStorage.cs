using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

public class InMemoryJobStorage : IJobStorage
{
    private readonly int _maxCapacity;

    // Using ConcurrentDictionary for thread safety
    private readonly ConcurrentDictionary<string, JobStorageDto> _jobs = new();

    // Internal state for queues (Pause status etc)
    private readonly ConcurrentDictionary<string, QueueInfo> _queues = new();

    public InMemoryJobStorage(InMemoryStorageOptions options)
    {
        _maxCapacity = options.MaxCapacity;

        // Initialize default queue
        _queues.TryAdd("default", new QueueInfo("default"));
    }

    public ValueTask<JobStorageDto?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        _jobs.TryGetValue(jobId, out var job);
        return new ValueTask<JobStorageDto?>(job);
    }

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
        // 1. Create DTO using positional record constructor
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

        // 2. Save with eviction policy
        SaveJobInternal(jobDto);

        return new ValueTask<string>(id);
    }

    private void SaveJobInternal(JobStorageDto job)
    {
        // SMART EVICTION POLICY
        if (_jobs.Count >= _maxCapacity)
        {
            // A. Remove finished jobs first
            var jobsToRemove = _jobs.Values
                .Where(x => x.Status == JobStatus.Succeeded || x.Status == JobStatus.Failed || x.Status == JobStatus.Cancelled)
                .OrderBy(x => x.CreatedAtUtc)
                .Take(1000)
                .Select(x => x.Id)
                .ToList();

            foreach (var id in jobsToRemove) _jobs.TryRemove(id, out _);

            // B. If still full, remove oldest ANY jobs
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

        // Ensure queue is registered
        if (!_queues.ContainsKey(job.Queue))
        {
            _queues.TryAdd(job.Queue, new QueueInfo(job.Queue));
        }
    }

    public ValueTask<bool> UpdateJobStateAsync(string jobId, JobStatus status, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            // Records are immutable, use 'with' to create a copy
            var updatedJob = job with
            {
                Status = status,
                StartedAtUtc = status == JobStatus.Processing ? DateTime.UtcNow : job.StartedAtUtc,
                FinishedAtUtc = (status == JobStatus.Succeeded || status == JobStatus.Failed || status == JobStatus.Cancelled) ? DateTime.UtcNow : job.FinishedAtUtc,
                LastUpdatedUtc = DateTime.UtcNow
            };

            _jobs[jobId] = updatedJob;
            return new ValueTask<bool>(true);
        }
        return new ValueTask<bool>(false);
    }

    public ValueTask UpdateJobPriorityAsync(string jobId, int priority, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            var updatedJob = job with { Priority = priority, LastUpdatedUtc = DateTime.UtcNow };
            _jobs[jobId] = updatedJob;
        }
        return ValueTask.CompletedTask;
    }

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

    public ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(string workerId, int limit, string[]? allowedQueues, CancellationToken ct = default)
    {
        // Filter jobs
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
            // Lock logic (simulate transaction)
            var processingJob = job with
            {
                Status = JobStatus.Processing,
                WorkerId = workerId,
                StartedAtUtc = DateTime.UtcNow,
                LastUpdatedUtc = DateTime.UtcNow,
                AttemptCount = job.AttemptCount + 1
            };

            if (_jobs.TryUpdate(job.Id, processingJob, job))
            {
                lockedJobs.Add(processingJob);
            }
        }

        return new ValueTask<IEnumerable<JobStorageDto>>(lockedJobs);
    }

    public ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(int limit, CancellationToken ct = default)
    {
        var result = _jobs.Values
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(limit);

        return new ValueTask<IEnumerable<JobStorageDto>>(result);
    }

    public ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default)
    {
        // Aggregate stats
        var stats = _jobs.Values
            .GroupBy(j => j.Queue)
            .Select(g => new QueueDto(
                Name: g.Key,
                IsPaused: _queues.TryGetValue(g.Key, out var q) && q.IsPaused,
                PendingCount: g.Count(j => j.Status == JobStatus.Pending),
                ProcessingCount: g.Count(j => j.Status == JobStatus.Processing),
                FailedCount: g.Count(j => j.Status == JobStatus.Failed),
                SucceededCount: g.Count(j => j.Status == JobStatus.Succeeded),
                FirstJobAtUtc: g.Min(j => j.StartedAtUtc),
                LastJobAtUtc: g.Max(j => j.FinishedAtUtc)
            ))
            .ToList();

        // Add empty queues
        foreach (var q in _queues.Values)
        {
            if (!stats.Any(s => s.Name == q.Name))
            {
                stats.Add(new QueueDto(q.Name, q.IsPaused, 0, 0, 0, 0, null, null));
            }
        }

        return new ValueTask<IEnumerable<QueueDto>>(stats);
    }

    // Implements IJobStorage.SetQueueStateAsync
    public ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueInfo(queueName) { IsPaused = isPaused, LastUpdatedUtc = DateTime.UtcNow },
            (key, old) => { old.IsPaused = isPaused; old.LastUpdatedUtc = DateTime.UtcNow; return old; });

        return ValueTask.CompletedTask;
    }

    // ==========================================
    // Internal Helper Class (Fixes "QueueInfo not found")
    // ==========================================
    private class QueueInfo
    {
        public string Name { get; }
        public bool IsPaused { get; set; }
        public DateTime LastUpdatedUtc { get; set; }

        public QueueInfo(string name)
        {
            Name = name;
            LastUpdatedUtc = DateTime.UtcNow;
        }
    }
}