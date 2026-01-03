using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.Storage;

/// <summary>
/// Defines the contract for job persistence providers.
/// Supports Pluggable Provider Model (Strategy Pattern).
/// </summary>
public interface IJobStorage
{
    /// <summary>
    /// Persists a new job into the storage.
    /// </summary>
    /// <param name="queue">Target queue name.</param>
    /// <param name="jobType">Fully qualified type name of the job.</param>
    /// <param name="payload">Serialized job parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unique ID of the created job.</returns>
    ValueTask<string> CreateJobAsync(
        string id, // <--- NEW param
        string queue,
        string jobType,
        string payload,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a job by its unique identifier.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The job data DTO or null if not found.</returns>
    ValueTask<JobStorageDto?> GetJobAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Updates the status of an existing job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="status">New status to set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if updated, False if job not found.</returns>
    ValueTask<bool> UpdateJobStateAsync(string id, JobStatus status, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a paginated list of jobs for monitoring purposes.
    /// </summary>
    /// <param name="limit">Max number of jobs to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A collection of job DTOs.</returns>
    ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(int limit = 50, CancellationToken ct = default);
}