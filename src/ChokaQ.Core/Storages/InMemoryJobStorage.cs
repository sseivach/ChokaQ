using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Storages;

/// <summary>
/// In-memory implementation of IJobStorage.
/// Useful for testing and development, but not recommended for production
/// as data is lost when the application restarts.
/// </summary>
public class InMemoryJobStorage : IJobStorage
{
    private readonly ConcurrentDictionary<string, JobStorageDto> _jobs = new();
    private readonly ILogger<InMemoryJobStorage> _logger;
    private readonly TimeProvider _timeProvider;

    public InMemoryJobStorage(ILogger<InMemoryJobStorage> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
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
        // Use UtcDateTime to align with the DTO change
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Calculate schedule time if delay is provided
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
            return new ValueTask<string>(id);
        }

        // Handle unlikely ID collision
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
    public ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(string workerId, int limit, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var lockedJobs = new List<JobStorageDto>();

        // Simulation of transactional locking for In-Memory storage.
        // NOTE: This is not strictly thread-safe in a high-concurrency scenario without global locks,
        // but sufficient for local development and testing.

        // 1. Find candidates: Pending, Schedule reached, Ordered by Priority
        var candidates = _jobs.Values
            .Where(j => j.Status == JobStatus.Pending && (!j.ScheduledAtUtc.HasValue || j.ScheduledAtUtc <= now))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.ScheduledAtUtc)
            .ThenBy(j => j.CreatedAtUtc)
            .Take(limit)
            .ToList();

        // 2. Try to lock (update status to Processing)
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

            // Use TryUpdate to ensure we don't pick up a job that another thread just modified
            if (_jobs.TryUpdate(job.Id, updated, job))
            {
                lockedJobs.Add(updated);
            }
        }

        return new ValueTask<IEnumerable<JobStorageDto>>(lockedJobs);
    }
}