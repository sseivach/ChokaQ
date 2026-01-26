using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

/// <summary>
/// The "Three Pillars" Storage Contract.
/// STRICT MODE: Explicit state transitions only.
/// Includes High-Performance Batch Fetching.
/// </summary>
public interface IJobStorage
{
    // --- 1. Production Flow (Hot Path) ---

    ValueTask CreateJobAsync(JobStorageDto job, CancellationToken ct = default);

    /// <summary>
    /// Fetches a BATCH of jobs into RAM for high-throughput processing.
    /// Transition: Pending -> Processing (Atomic).
    /// </summary>
    ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(string workerId, int limit, string[]? allowedQueues, CancellationToken ct = default);

    /// <summary>
    /// Updates the heartbeat to keep the job alive during processing.
    /// </summary>
    ValueTask UpdateHeartbeatAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Finalizes a job by moving it out of Hot storage.
    /// Transaction: Delete from Hot -> Insert into Archive (Success) or Morgue (Failure).
    /// </summary>
    ValueTask ArchiveJobAsync(string jobId, JobStatus finalStatus, string? error = null, CancellationToken ct = default);

    /// <summary>
    /// Re-queues a job for a future attempt (Soft Failure).
    /// Transition: Processing -> Pending (with delay).
    /// </summary>
    ValueTask ScheduleRetryAsync(string jobId, DateTime nextAttemptUtc, int attemptCount, CancellationToken ct = default);

    // --- 2. Resilience ---

    /// <summary>
    /// Detects dead workers and resets their jobs.
    /// </summary>
    ValueTask<int> RescueZombiesAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Manually revives a job from the Dead Letter Queue (Morgue).
    /// </summary>
    ValueTask ResurrectJobAsync(string jobId, string? newPayload = null, string? newTags = null, CancellationToken ct = default);

    // --- 3. Read Models (Dashboard) ---

    ValueTask<JobStorageDto?> GetJobAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Polymorphic Query: Returns jobs from Hot, Archive, or Morgue based on status.
    /// </summary>
    ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(JobStatus status, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// High-performance counters from the StatsSummary table.
    /// </summary>
    ValueTask<IEnumerable<QueueStatsDto>> GetSummaryStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Detailed queue configuration and real-time active metrics.
    /// </summary>
    ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default);

    // --- 4. Admin Ops ---

    ValueTask UpdatePayloadAsync(string jobId, string payload, string? tags = null, CancellationToken ct = default);
    ValueTask UpdateJobPriorityAsync(string id, int newPriority, CancellationToken ct = default);
    ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default);
    ValueTask UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default);
}