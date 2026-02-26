using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.Storage;

/// <summary>
/// Defines the contract for job persistence providers.
/// Implements the "Three Pillars" architecture: Hot, Archive, DLQ.
/// </summary>
public interface IJobStorage
{
    // ========================================================================
    // CORE OPERATIONS (Hot Table)
    // ========================================================================

    /// <summary>
    /// Creates a new job in the Hot table with Pending status.
    /// </summary>
    /// <returns>The job ID.</returns>
    ValueTask<string> EnqueueAsync(
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
    /// Atomically fetches and locks the next batch of pending jobs for a worker.
    /// Sets Status = Fetched and assigns WorkerId.
    /// </summary>
    ValueTask<IEnumerable<JobHotEntity>> FetchNextBatchAsync(
        string workerId,
        int batchSize,
        string[]? allowedQueues = null,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions a job from Fetched to Processing status.
    /// Sets StartedAtUtc and HeartbeatUtc.
    /// </summary>
    ValueTask MarkAsProcessingAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Updates the heartbeat timestamp for an active job.
    /// Used for zombie detection.
    /// </summary>
    ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a job from the Hot table by ID.
    /// </summary>
    ValueTask<JobHotEntity?> GetJobAsync(string jobId, CancellationToken ct = default);

    // ========================================================================
    // ATOMIC TRANSITIONS (Three Pillars)
    // ========================================================================

    /// <summary>
    /// Atomically archives a succeeded job: Hot → Archive.
    /// Uses OUTPUT clause for data integrity.
    /// Increments StatsSummary.SucceededTotal.
    /// </summary>
    ValueTask ArchiveSucceededAsync(
        string jobId,
        double? durationMs = null,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically moves a failed job to DLQ: Hot → DLQ.
    /// Uses OUTPUT clause for data integrity.
    /// Increments StatsSummary.FailedTotal.
    /// </summary>
    /// <param name="jobId">The job ID.</param>
    /// <param name="errorDetails">Exception details or failure reason.</param>
    ValueTask ArchiveFailedAsync(
        string jobId,
        string errorDetails,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically moves a cancelled job to DLQ: Hot → DLQ.
    /// Uses OUTPUT clause for data integrity.
    /// </summary>
    ValueTask ArchiveCancelledAsync(
        string jobId,
        string? cancelledBy = null,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically moves a zombie job to DLQ: Hot → DLQ.
    /// Called by ZombieRescueService.
    /// </summary>
    ValueTask ArchiveZombieAsync(
        string jobId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically resurrects a job from DLQ: DLQ → Hot.
    /// Resets AttemptCount to 0, Status to Pending.
    /// Optionally applies data updates (Payload, Tags, Priority).
    /// Decrements StatsSummary.FailedTotal.
    /// </summary>
    /// <param name="jobId">The job ID in DLQ.</param>
    /// <param name="updates">Optional data modifications.</param>
    /// <param name="resurrectedBy">Admin identity for audit.</param>
    ValueTask ResurrectAsync(
        string jobId,
        JobDataUpdateDto? updates = null,
        string? resurrectedBy = null,
        CancellationToken ct = default);

    /// <summary>
    /// Bulk resurrects multiple jobs from DLQ.
    /// Processes in batches of 1000 for transaction safety.
    /// </summary>
    /// <returns>Number of jobs successfully resurrected.</returns>
    ValueTask<int> ResurrectBatchAsync(
        string[] jobIds,
        string? resurrectedBy = null,
        CancellationToken ct = default);

    /// <summary>
    /// Releases a fetched job back to the queue (Pending status) without processing it.
    /// This is used when a queue is paused, but the worker has already buffered the job.
    /// The attempt count should be decremented or preserved, not incremented.
    /// </summary>
    /// <param name="jobId">The unique job identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ReleaseJobAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Atomically moves a batch of cancelled jobs to DLQ: Hot → DLQ.
    /// Only affects jobs in Pending or Fetched status.
    /// </summary>
    ValueTask<int> ArchiveCancelledBatchAsync(
        string[] jobIds,
        string? cancelledBy = null,
        CancellationToken ct = default);

    // ========================================================================
    // RETRY LOGIC (Stays in Hot)
    // ========================================================================

    /// <summary>
    /// Reschedules a failed job for retry within the Hot table.
    /// Increments AttemptCount, sets ScheduledAtUtc, Status = Pending.
    /// Increments StatsSummary.RetriedTotal.
    /// </summary>
    ValueTask RescheduleForRetryAsync(
        string jobId,
        DateTime scheduledAtUtc,
        int newAttemptCount,
        string lastError,
        CancellationToken ct = default);

    // ========================================================================
    // DIVINE MODE (Admin Operations)
    // ========================================================================

    /// <summary>
    /// Updates job data for a Pending job in Hot table.
    /// Safety Gate: Fails with false if Status != Pending.
    /// </summary>
    /// <returns>True if updated, false if job not found or not in Pending status.</returns>
    ValueTask<bool> UpdateJobDataAsync(
        string jobId,
        JobDataUpdateDto updates,
        string? modifiedBy = null,
        CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes jobs from DLQ.
    /// Use with caution - data is unrecoverable.
    /// </summary>
    ValueTask PurgeDLQAsync(
        string[] jobIds,
        CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes old jobs from Archive.
    /// </summary>
    /// <param name="olderThan">Delete jobs finished before this date.</param>
    /// <returns>Number of jobs deleted.</returns>
    ValueTask<int> PurgeArchiveAsync(
        DateTime olderThan,
        CancellationToken ct = default);

    /// <summary>
    /// Updates job data in DLQ without resurrecting it.
    /// Note: Priority cannot be updated in DLQ as it is a scheduling property.
    /// </summary>
    ValueTask<bool> UpdateDLQJobDataAsync(
        string jobId,
        JobDataUpdateDto updates,
        string? modifiedBy = null,
        CancellationToken ct = default);

    // ========================================================================
    // OBSERVABILITY (Dashboard)
    // ========================================================================

    /// <summary>
    /// Gets hybrid statistics: StatsSummary (totals) + Hot counts.
    /// O(1) read from StatsSummary + fast COUNT on Hot.
    /// Returns aggregated stats (Queue = null for all queues).
    /// </summary>
    ValueTask<StatsSummaryEntity> GetSummaryStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets per-queue statistics: StatsSummary (totals) + Hot counts per queue.
    /// Returns stats for each queue separately (Queue field is not null).
    /// </summary>
    ValueTask<IEnumerable<StatsSummaryEntity>> GetQueueStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves active jobs from Hot table for dashboard.
    /// </summary>
    ValueTask<IEnumerable<JobHotEntity>> GetActiveJobsAsync(
        int limit = 100,
        JobStatus? statusFilter = null,
        string? queueFilter = null,
        string? searchTerm = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves succeeded jobs from Archive for history view.
    /// </summary>
    ValueTask<IEnumerable<JobArchiveEntity>> GetArchiveJobsAsync(
        int limit = 100,
        string? queueFilter = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? tagFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves failed jobs from DLQ for morgue view.
    /// </summary>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="queueFilter">Filter by queue name.</param>
    /// <param name="reasonFilter">Filter by failure reason (Cancelled, Zombie, etc.).</param>
    /// <param name="searchTerm">Search in Id, Type, Tags, ErrorDetails.</param>
    /// <param name="fromDate">Filter jobs created on or after this date.</param>
    /// <param name="toDate">Filter jobs created on or before this date.</param>
    ValueTask<IEnumerable<JobDLQEntity>> GetDLQJobsAsync(
        int limit = 100,
        string? queueFilter = null,
        FailureReason? reasonFilter = null,
        string? searchTerm = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a single job from Archive by ID.
    /// </summary>
    ValueTask<JobArchiveEntity?> GetArchiveJobAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Gets a single job from DLQ by ID.
    /// </summary>
    ValueTask<JobDLQEntity?> GetDLQJobAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a paginated list of archived jobs based on complex filters.
    /// </summary>
    ValueTask<PagedResult<JobArchiveEntity>> GetArchivePagedAsync(
        HistoryFilterDto filter,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a paginated list of DLQ jobs based on complex filters.
    /// </summary>
    ValueTask<PagedResult<JobDLQEntity>> GetDLQPagedAsync(
        HistoryFilterDto filter,
        CancellationToken ct = default);

    // ========================================================================
    // QUEUE MANAGEMENT
    // ========================================================================

    /// <summary>
    /// Gets all queue configurations with live stats.
    /// </summary>
    ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default);

    /// <summary>
    /// Pauses or resumes a queue.
    /// </summary>
    ValueTask SetQueuePausedAsync(
        string queueName,
        bool isPaused,
        CancellationToken ct = default);

    /// <summary>
    /// Updates zombie timeout for a queue.
    /// </summary>
    ValueTask SetQueueZombieTimeoutAsync(
        string queueName,
        int? timeoutSeconds,
        CancellationToken ct = default);

    ValueTask SetQueueActiveAsync(
        string queueName,
        bool isActive,
        CancellationToken ct = default);

    // ========================================================================
    // RECOVERY & ZOMBIE DETECTION
    // ========================================================================

    /// <summary>
    /// Recovers abandoned jobs (stuck in Fetched state) by reverting them to Pending.
    /// This happens when a worker crashes after fetching but before processing.
    /// </summary>
    /// <returns>Number of recovered jobs.</returns>
    ValueTask<int> RecoverAbandonedAsync(int timeoutSeconds, CancellationToken ct = default);

    /// <summary>
    /// Finds and archives true zombie jobs (Processing with expired heartbeat) to the DLQ.
    /// </summary>
    /// <param name="globalTimeoutSeconds">Default timeout if queue-specific not set.</param>
    /// <returns>Number of zombies archived to DLQ.</returns>
    ValueTask<int> ArchiveZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default);
}