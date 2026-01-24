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

    private void SaveJobInternal(JobStorageDto job)
    {
        // Simple eviction to prevent OOM
        if (_jobs.Count >= _maxCapacity)
        {
            var jobsToRemove = _jobs.Values
                .Where(x => x.Status == JobStatus.Succeeded || x.Status == JobStatus.Failed || x.Status == JobStatus.Cancelled)
                .OrderBy(x => x.CreatedAtUtc)
                .Take(1000)
                .Select(x => x.Id)
                .ToList();

            foreach (var id in jobsToRemove) _jobs.TryRemove(id, out _);

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

        if (!_queues.ContainsKey(job.Queue))
            _queues.TryAdd(job.Queue, new QueueInfo(job.Queue));
    }

    public ValueTask<bool> UpdateJobStateAsync(string jobId, JobStatus status, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
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

    // 👇 IMPLEMNTED MISSING METHOD 👇
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
            // This mimics the SQL logic: Worker buffer holds "Fetched" jobs.
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

    public ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(int limit, CancellationToken ct = default)
    {
        var result = _jobs.Values.OrderByDescending(j => j.CreatedAtUtc).Take(limit);
        return new ValueTask<IEnumerable<JobStorageDto>>(result);
    }

    public ValueTask<JobCountsDto> GetJobCountsAsync(CancellationToken ct = default)
    {
        var values = _jobs.Values;
        var counts = new JobCountsDto(
            Pending: values.Count(x => x.Status == JobStatus.Pending),
            Fetched: values.Count(x => x.Status == JobStatus.Fetched),       // <--- Count Fetched
            Processing: values.Count(x => x.Status == JobStatus.Processing),
            Succeeded: values.Count(x => x.Status == JobStatus.Succeeded),
            Failed: values.Count(x => x.Status == JobStatus.Failed),
            Cancelled: values.Count(x => x.Status == JobStatus.Cancelled),
            Total: values.Count
        );
        return new ValueTask<JobCountsDto>(counts);
    }

    public ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default)
    {
        var stats = _jobs.Values
            .GroupBy(j => j.Queue)
            .Select(g => new QueueDto(
                Name: g.Key,
                IsPaused: _queues.TryGetValue(g.Key, out var q) && q.IsPaused,
                PendingCount: g.Count(j => j.Status == JobStatus.Pending),
                FetchedCount: g.Count(j => j.Status == JobStatus.Fetched),       // <--- NEW
                ProcessingCount: g.Count(j => j.Status == JobStatus.Processing),
                FailedCount: g.Count(j => j.Status == JobStatus.Failed),
                SucceededCount: g.Count(j => j.Status == JobStatus.Succeeded),
                FirstJobAtUtc: g.Min(j => j.StartedAtUtc),
                LastJobAtUtc: g.Max(j => j.FinishedAtUtc)
            ))
            .ToList();

        foreach (var q in _queues.Values)
        {
            if (!stats.Any(s => s.Name == q.Name))
                stats.Add(new QueueDto(q.Name, q.IsPaused, 0, 0, 0, 0, 0, null, null));
        }
        return new ValueTask<IEnumerable<QueueDto>>(stats);
    }

    public ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueInfo(queueName) { IsPaused = isPaused, LastUpdatedUtc = DateTime.UtcNow },
            (key, old) => { old.IsPaused = isPaused; old.LastUpdatedUtc = DateTime.UtcNow; return old; });
        return ValueTask.CompletedTask;
    }

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