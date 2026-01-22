using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// In-memory implementation of IJobStorage.
/// Useful for testing and development, but not recommended for production
/// as data is lost when the application restarts.
/// </summary>
public class InMemoryJobStorage : IJobStorage
{
    private readonly ConcurrentDictionary<string, JobStorageDto> _jobs = new();

    // Stores the paused state of queues: Key = QueueName, Value = IsPaused
    private readonly ConcurrentDictionary<string, bool> _queueStates = new();

    private readonly ILogger<InMemoryJobStorage> _logger;
    private readonly TimeProvider _timeProvider;

    public InMemoryJobStorage(ILogger<InMemoryJobStorage> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;

        // Ensure default queue exists and is running
        _queueStates.TryAdd("default", false);
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
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime? scheduledAt = delay.HasValue ? now.Add(delay.Value) : null;

        var job = new JobStorageDto(
            Id: id,
            Queue: queue,
            Type: jobType,
            Payload: payload,
            Status: JobStatus.Pending,
            AttemptCount: 0,
            Priority: priority,
            ScheduledAtUtc: scheduledAt,
            Tags: tags,
            IdempotencyKey: idempotencyKey,
            WorkerId: null,
            ErrorDetails: null,
            CreatedBy: createdBy,
            CreatedAtUtc: now,
            StartedAtUtc: null,
            FinishedAtUtc: null,
            LastUpdatedUtc: now
        );

        if (_jobs.TryAdd(id, job))
        {
            // Ensure queue is tracked
            _queueStates.TryAdd(queue, false);
            return new ValueTask<string>(id);
        }

        var ex = new InvalidOperationException($"Job ID collision detected: {id}");
        _logger.LogError(ex, "Failed to create job in memory.");
        throw ex;
    }

    /// <inheritdoc />
    public ValueTask<JobStorageDto?> GetJobAsync(string id, CancellationToken ct = default)
    {
        _jobs.TryGetValue(id, out var job);
        return new ValueTask<JobStorageDto?>(job);
    }

    /// <inheritdoc />
    public ValueTask<bool> UpdateJobStateAsync(string id, JobStatus status, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(id, out var existing)) return new ValueTask<bool>(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var updated = existing with
        {
            Status = status,
            LastUpdatedUtc = now,
            FinishedAtUtc = (status == JobStatus.Succeeded || status == JobStatus.Failed || status == JobStatus.Cancelled)
                ? now
                : existing.FinishedAtUtc
        };

        return new ValueTask<bool>(_jobs.TryUpdate(id, updated, existing));
    }

    /// <inheritdoc />
    public ValueTask<bool> IncrementJobAttemptAsync(string id, int newAttemptCount, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(id, out var existing)) return new ValueTask<bool>(false);

        var updated = existing with
        {
            AttemptCount = newAttemptCount,
            LastUpdatedUtc = _timeProvider.GetUtcNow().UtcDateTime
        };

        return new ValueTask<bool>(_jobs.TryUpdate(id, updated, existing));
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        var items = _jobs.Values.OrderByDescending(x => x.CreatedAtUtc).Take(limit);
        return new ValueTask<IEnumerable<JobStorageDto>>(items);
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(
        string workerId,
        int limit,
        string[]? allowedQueues, // <--- Updated
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var lockedJobs = new List<JobStorageDto>();

        // Convert array to HashSet for O(1) lookups
        var allowedSet = allowedQueues != null
            ? new HashSet<string>(allowedQueues)
            : new HashSet<string>();

        if (allowedSet.Count == 0) return new ValueTask<IEnumerable<JobStorageDto>>(lockedJobs);

        var candidates = _jobs.Values
            .Where(j => j.Status == JobStatus.Pending &&
                        (!j.ScheduledAtUtc.HasValue || j.ScheduledAtUtc <= now) &&
                        allowedSet.Contains(j.Queue)) // <--- Logic used directly here
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.ScheduledAtUtc)
            .ThenBy(j => j.CreatedAtUtc)
            .Take(limit)
            .ToList();

        foreach (var job in candidates)
        {
            var updated = job with
            {
                Status = JobStatus.Processing,
                WorkerId = workerId,
                StartedAtUtc = now,
                LastUpdatedUtc = now,
                AttemptCount = job.AttemptCount + 1
            };

            if (_jobs.TryUpdate(job.Id, updated, job))
            {
                lockedJobs.Add(updated);
            }
        }

        return new ValueTask<IEnumerable<JobStorageDto>>(lockedJobs);
    }

    /// <summary>
    /// [NEW] In-Memory implementation of Queue Management.
    /// Calculates stats on the fly from the dictionary.
    /// </summary>
    public ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default)
    {
        // 1. Identify all known queues (from jobs + explicitly tracked)
        var queueNames = _jobs.Values.Select(j => j.Queue)
            .Union(_queueStates.Keys)
            .Distinct()
            .ToList();

        var result = new List<QueueDto>();

        foreach (var qName in queueNames)
        {
            // Ensure state is tracked
            _queueStates.TryGetValue(qName, out bool isPaused);

            // Aggregate counts
            var pending = _jobs.Values.Count(j => j.Queue == qName && j.Status == JobStatus.Pending);
            var processing = _jobs.Values.Count(j => j.Queue == qName && j.Status == JobStatus.Processing);
            var failed = _jobs.Values.Count(j => j.Queue == qName && j.Status == JobStatus.Failed);

            result.Add(new QueueDto(qName, isPaused, pending, processing, failed));
        }

        return new ValueTask<IEnumerable<QueueDto>>(result);
    }

    /// <summary>
    /// [NEW] Updates the paused state of a queue in memory.
    /// </summary>
    public ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        _queueStates.AddOrUpdate(queueName, isPaused, (key, oldValue) => isPaused);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateJobPriorityAsync(string id, int newPriority, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(id, out var existing))
        {
            var updated = existing with
            {
                Priority = newPriority,
                LastUpdatedUtc = _timeProvider.GetUtcNow().UtcDateTime
            };
            _jobs.TryUpdate(id, updated, existing);
        }
        return ValueTask.CompletedTask;
    }
}