using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

/// <summary>
/// Defines the contract for job persistence providers.
/// Supports Pluggable Provider Model (Strategy Pattern).
/// </summary>
public interface IJobStorage
{
    /// <summary>
    /// Persists a new job into the storage.
    /// </summary>
    ValueTask<string> CreateJobAsync(
        string id,
        string queue,
        string jobType,
        string payload,
        int priority = 10,
        string? createdBy = null,
        string? tags = null,
        TimeSpan? delay = null,
        string? idempotencyKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a job by its unique identifier.
    /// </summary>
    ValueTask<JobStorageDto?> GetJobAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Updates the status of an existing job.
    /// </summary>
    ValueTask<bool> UpdateJobStateAsync(string id, JobStatus status, CancellationToken ct = default);

    /// <summary>
    /// Updates the attempt counter for a job.
    /// </summary>
    ValueTask<bool> IncrementJobAttemptAsync(string id, int newAttemptCount, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a paginated list of jobs for monitoring purposes.
    /// </summary>
    ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Retrieves global statistics (counts per status) efficiently.
    /// Used for dashboard headers without loading all job rows.
    /// </summary>
    ValueTask<JobCountsDto> GetJobCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically retrieves the next batch of pending jobs and locks them for the specific worker.
    /// Used by the Polling mechanism to prevent race conditions.
    /// </summary>
    ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(
        string workerId,
        int limit,
        string[]? allowedQueues,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the list of all active queues and their stats.
    /// </summary>
    ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default);

    /// <summary>
    /// Pauses or Resumes a specific queue.
    /// </summary>
    ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default);

    ValueTask UpdateJobPriorityAsync(string id, int newPriority, CancellationToken ct = default);
}