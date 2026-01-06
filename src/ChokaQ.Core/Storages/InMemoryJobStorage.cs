using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Storages;

/// <summary>
/// Uses ConcurrentDictionary for thread-safe operations without locking overhead.
/// </summary>
public class InMemoryJobStorage : IJobStorage
{
    // The "Vault". Holds all job data in memory.
    private readonly ConcurrentDictionary<string, JobStorageDto> _jobs = new();
    private readonly ILogger<InMemoryJobStorage> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the InMemoryJobStorage.
    /// </summary>
    /// <param name="logger">Logger instance for telemetry.</param>
    /// <param name="timeProvider">Abstraction for time access (testability).</param>
    public InMemoryJobStorage(
        ILogger<InMemoryJobStorage> logger,
        TimeProvider timeProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public ValueTask<string> CreateJobAsync(
        string id, // <--- Take ID from outside
        string queue,
        string jobType,
        string payload,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();

        var job = new JobStorageDto(
            Id: id,
            Queue: queue,
            Type: jobType,
            Payload: payload,
            Status: JobStatus.Pending,
            AttemptCount: 0,
            CreatedAtUtc: now,
            LastUpdatedUtc: now
        );

        if (_jobs.TryAdd(id, job))
        {
            _logger.LogInformation("Job created in memory. ID: {JobId}, Queue: {Queue}", id, queue);
            return new ValueTask<string>(id);
        }

        // Technically impossible with Guid.NewGuid(), but defensive coding is key.
        var ex = new InvalidOperationException($"Failed to generate unique ID for job. ID collision: {id}");
        _logger.LogError(ex, "Critical failure during job creation.");
        throw ex;
    }

    /// <inheritdoc />
    public ValueTask<JobStorageDto?> GetJobAsync(string id, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            return new ValueTask<JobStorageDto?>(job);
        }

        _logger.LogDebug("Job not found in memory storage. ID: {JobId}", id);
        return new ValueTask<JobStorageDto?>(result: null);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public ValueTask<bool> UpdateJobStateAsync(string id, JobStatus status, CancellationToken ct = default)
    {
        // CAS (Compare-And-Swap) Loop.
        // We read, modify, and try to update ONLY if the value hasn't changed in the meantime.
        while (true)
        {
            if (!_jobs.TryGetValue(id, out var existingJob))
            {
                _logger.LogWarning("Attempted to update non-existent job. ID: {JobId}", id);
                return new ValueTask<bool>(false);
            }

            var updatedJob = existingJob with
            {
                Status = status,
                LastUpdatedUtc = _timeProvider.GetUtcNow()
            };

            // Critical: TryUpdate ensures atomic replacement.
            // comparisonValue: existingJob (what we read just a moment ago).
            // If the dictionary value changed since we read it, TryUpdate returns false.
            if (_jobs.TryUpdate(id, updatedJob, existingJob))
            {
                _logger.LogInformation("Job state updated. ID: {JobId}, NewStatus: {Status}", id, status);
                return new ValueTask<bool>(true);
            }

            // If we are here, another thread modified the job while we were thinking.
            // We simply loop back, re-read the NEW state, and try again.
            // No sleep needed, this spin is extremely fast.
        }
    }

    // Implementation of IncrementJobAttemptAsync
    public ValueTask<bool> IncrementJobAttemptAsync(string id, int newAttemptCount, CancellationToken ct = default)
    {
        while (true)
        {
            if (!_jobs.TryGetValue(id, out var existing)) return new ValueTask<bool>(false);

            // Update only the counter and timestamp
            var updated = existing with
            {
                AttemptCount = newAttemptCount,
                LastUpdatedUtc = _timeProvider.GetUtcNow()
            };

            if (_jobs.TryUpdate(id, updated, existing)) return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        // Snapshot the values. 
        // In a real DB (SQL), this would be a SELECT * ORDER BY CreatedAt DESC LIMIT @limit
        // For InMemory, LINQ is fine for now.
        var items = _jobs.Values
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(limit);

        return new ValueTask<IEnumerable<JobStorageDto>>(items);
    }
}