using System.Collections.Concurrent;
using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Core.Storage;

public class InMemoryJobStorage : IJobStorage
{
    private readonly ConcurrentDictionary<string, JobEntity> _activeJobs = new();
    private readonly ConcurrentDictionary<string, JobSucceededEntity> _history = new();
    private readonly ConcurrentDictionary<string, JobMorgueEntity> _morgue = new();
    private readonly ConcurrentDictionary<string, QueueEntity> _queues = new();

    private readonly object _lock = new();

    public InMemoryJobStorage()
    {
        _queues.TryAdd("default", new QueueEntity { Name = "default", IsPaused = false });
    }

    // ========================================================================
    // 1. CORE
    // ========================================================================

    public ValueTask<string> EnqueueAsync(string queue, string jobType, string payload, int priority = 10, string? createdBy = null, string? tags = null, TimeSpan? delay = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var job = new JobEntity
        {
            Id = id,
            Queue = queue,
            Type = jobType,
            Payload = payload,
            Priority = priority,
            Status = JobStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            RunAtUtc = DateTime.UtcNow.Add(delay ?? TimeSpan.Zero),
            CreatedBy = createdBy
        };

        _activeJobs.TryAdd(id, job);

        if (!_queues.ContainsKey(queue))
            _queues.TryAdd(queue, new QueueEntity { Name = queue, IsPaused = false });

        return new ValueTask<string>(id);
    }

    public ValueTask<IEnumerable<JobEntity>> FetchNextBatchAsync(string workerId, int limit, string[]? allowedQueues, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var jobs = _activeJobs.Values
                .Where(j => j.Status == JobStatus.Pending && j.RunAtUtc <= DateTime.UtcNow)
                .Where(j => allowedQueues == null || allowedQueues.Contains(j.Queue))
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.CreatedAtUtc)
                .Take(limit)
                .ToList();

            foreach (var job in jobs)
            {
                job.Status = JobStatus.Fetched;
                job.LockedBy = workerId;
                job.LockedAtUtc = DateTime.UtcNow;
            }

            return new ValueTask<IEnumerable<JobEntity>>(jobs);
        }
    }

    /// <summary>
    /// FIX: Добавлен обязательный метод перехода в статус Processing (2)
    /// </summary>
    public Task MarkAsProcessingAsync(string jobId, CancellationToken ct = default)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            lock (_lock)
            {
                job.Status = JobStatus.Processing;
                job.LockedAtUtc = DateTime.UtcNow;
            }
        }
        return Task.CompletedTask;
    }

    public ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
            job.LockedAtUtc = DateTime.UtcNow;
        return ValueTask.CompletedTask;
    }

    // ========================================================================
    // 2. TRANSITIONS
    // ========================================================================

    public Task RetryJobAsync(string jobId, int nextAttempt, TimeSpan delay, string? lastError, CancellationToken ct = default)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            lock (_lock)
            {
                job.Status = JobStatus.Pending;
                job.AttemptCount = nextAttempt;
                job.RunAtUtc = DateTime.UtcNow.Add(delay);
                job.LastError = lastError;
                job.LockedBy = null;
                job.LockedAtUtc = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task ArchiveAsSuccessAsync(JobSucceededEntity record, CancellationToken ct = default)
    {
        _activeJobs.TryRemove(record.Id, out _);
        _history.TryAdd(record.Id, record);
        return Task.CompletedTask;
    }

    public Task ArchiveAsMorgueAsync(JobMorgueEntity record, CancellationToken ct = default)
    {
        _activeJobs.TryRemove(record.Id, out _);
        _morgue.TryAdd(record.Id, record);
        return Task.CompletedTask;
    }

    public Task ResurrectJobAsync(string jobId, CancellationToken ct = default)
    {
        if (_morgue.TryRemove(jobId, out var m))
        {
            var job = new JobEntity { Id = m.Id, Queue = m.Queue, Type = m.Type, Payload = m.Payload, Status = JobStatus.Pending, CreatedAtUtc = m.CreatedAtUtc, RunAtUtc = DateTime.UtcNow, CreatedBy = m.CreatedBy };
            _activeJobs.TryAdd(jobId, job);
        }
        return Task.CompletedTask;
    }

    // ========================================================================
    // 3. DIVINE MODE & OBSERVABILITY
    // ========================================================================

    public ValueTask<JobCountsDto> GetJobCountsAsync(CancellationToken ct = default)
    {
        var counts = new JobCountsDto(
            Pending: _activeJobs.Values.Count(x => x.Status == JobStatus.Pending),
            Processing: _activeJobs.Values.Count(x => x.Status == JobStatus.Processing || x.Status == JobStatus.Fetched),
            Succeeded: _history.Count,
            Failed: _morgue.Count,
            Retrying: _activeJobs.Values.Count(x => x.AttemptCount > 0 && x.Status == JobStatus.Pending),
            Scheduled: _activeJobs.Values.Count(x => x.RunAtUtc > DateTime.UtcNow),
            Zombies: 0
        );
        return new ValueTask<JobCountsDto>(counts);
    }

    public ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default)
    {
        var result = _queues.Values.Select(q => {
            q.PendingCount = _activeJobs.Values.Count(j => j.Queue == q.Name && j.Status == JobStatus.Pending);
            q.ProcessingCount = _activeJobs.Values.Count(j => j.Queue == q.Name && (j.Status == JobStatus.Processing || j.Status == JobStatus.Fetched));
            q.SucceededCount = _history.Values.Count(j => j.Queue == q.Name);
            q.FailedCount = _morgue.Values.Count(j => j.Queue == q.Name);
            return q;
        }).ToList();
        return new ValueTask<IEnumerable<QueueEntity>>(result);
    }

    /// <summary>
    /// FIX: Реализация редактирования задачи
    /// </summary>
    public Task UpdateJobDataAsync(JobDataUpdateDto update, CancellationToken ct = default)
    {
        if (_activeJobs.TryGetValue(update.JobId, out var job) && job.Status == JobStatus.Pending)
        {
            lock (_lock)
            {
                job.Payload = update.NewPayload;
                job.Priority = update.NewPriority;
            }
        }
        return Task.CompletedTask;
    }

    public Task UpdateJobPriorityAsync(string jobId, int priority, CancellationToken ct = default)
    {
        if (_activeJobs.TryGetValue(jobId, out var job)) job.Priority = priority;
        return Task.CompletedTask;
    }

    public Task UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default)
    {
        if (_queues.TryGetValue(queueName, out var q)) q.ZombieTimeoutSeconds = timeoutSeconds;
        return Task.CompletedTask;
    }

    public ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        if (_queues.TryGetValue(queueName, out var q)) q.IsPaused = isPaused;
        else _queues.TryAdd(queueName, new QueueEntity { Name = queueName, IsPaused = isPaused });
        return ValueTask.CompletedTask;
    }

    public Task PurgeJobAsync(string id, CancellationToken ct = default)
    {
        _activeJobs.TryRemove(id, out _);
        _history.TryRemove(id, out _);
        _morgue.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    // --- Lookups ---

    public ValueTask<IEnumerable<JobEntity>> GetActiveJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
        => new(_activeJobs.Values.Where(j => j.Queue == queue).OrderByDescending(j => j.CreatedAtUtc).Skip(skip).Take(take).ToList());

    public ValueTask<IEnumerable<JobSucceededEntity>> GetHistoryJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
        => new(_history.Values.Where(j => j.Queue == queue).OrderByDescending(j => j.FinishedAtUtc).Skip(skip).Take(take).ToList());

    public ValueTask<IEnumerable<JobMorgueEntity>> GetMorgueJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
        => new(_morgue.Values.Where(j => j.Queue == queue).OrderByDescending(j => j.FailedAtUtc).Skip(skip).Take(take).ToList());

    public ValueTask<JobEntity?> GetJobEntityAsync(string id, CancellationToken ct = default)
        => new(_activeJobs.GetValueOrDefault(id));

    public ValueTask<int> MarkZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default) => new(0);
    public ValueTask<StatsSummaryEntity?> GetSummaryStatsAsync(string queue, CancellationToken ct = default) => new((StatsSummaryEntity?)null);
    public ValueTask<JobSucceededEntity?> GetSucceededEntityAsync(string id, CancellationToken ct = default) => new(_history.GetValueOrDefault(id));
    public ValueTask<JobMorgueEntity?> GetMorgueEntityAsync(string id, CancellationToken ct = default) => new(_morgue.GetValueOrDefault(id));
}