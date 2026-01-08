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
    /// <param name="id">The unique job identifier.</param>
    /// <param name="queue">Target queue name.</param>
    /// <param name="jobType">Fully qualified type name of the job.</param>
    /// <param name="payload">Serialized job parameters.</param>
    /// <param name="priority">Job priority (default 10).</param>
    /// <param name="createdBy">Optional user/service identifier.</param>
    /// <param name="tags">Optional searchable tags.</param>
    /// <param name="delay">Optional delay before execution.</param>
    /// <param name="idempotencyKey">Optional key to prevent duplicates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unique ID of the created job.</returns>
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
    /// Atomically retrieves the next batch of pending jobs and locks them for the specific worker.
    /// Used by the Polling mechanism to prevent race conditions.
    /// </summary>
    /// <param name="workerId">The identifier of the worker requesting jobs.</param>
    /// <param name="limit">The maximum number of jobs to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A collection of locked job DTOs.</returns>
    ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(
        string workerId,
        int limit,
        CancellationToken ct = default);
}