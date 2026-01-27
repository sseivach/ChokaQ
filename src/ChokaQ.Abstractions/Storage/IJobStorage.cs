using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

public interface IJobStorage
{
    // ========================================================================
    // 1. CORE (Worker & Producer)
    // ========================================================================

    /// <summary>
    /// [1.4 Core] Enqueues a new job into the Active (Hot) table.
    /// </summary>
    ValueTask<string> EnqueueAsync(
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
    /// [1.4 Core] Fetches and locks a batch of jobs for a worker.
    /// </summary>
    ValueTask<IEnumerable<JobEntity>> FetchNextBatchAsync(
        string workerId,
        int limit,
        string[]? allowedQueues,
        CancellationToken ct = default);

    /// <summary>
    /// Prevents zombie jobs (Update Heartbeat).
    /// </summary>
    ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default);


    // ========================================================================
    // 2. TRANSITIONS (Atomic Move)
    // ========================================================================

    /// <summary>
    /// [Retry Logic] Keeps the job in Hot storage but schedules it for later.
    /// Used when a job fails transiently (e.g., API timeout) and has attempts left.
    /// </summary>
    Task RetryJobAsync(string jobId, int nextAttempt, TimeSpan delay, string? lastError, CancellationToken ct = default);

    /// <summary>
    /// [1.4 Transition] ArchiveJobAsync (Success variant).
    /// Atomically DELETE from Hot -> INSERT into Succeeded.
    /// </summary>
    Task ArchiveAsSuccessAsync(JobSucceededEntity archiveRecord, CancellationToken ct = default);

    /// <summary>
    /// [1.4 Transition] ArchiveJobAsync (Morgue variant).
    /// Atomically DELETE from Hot -> INSERT into Morgue.
    /// </summary>
    Task ArchiveAsMorgueAsync(JobMorgueEntity morgueRecord, CancellationToken ct = default);

    /// <summary>
    /// [1.4 Transition] ResurrectJobAsync.
    /// Atomically DELETE from Morgue -> INSERT into Hot.
    /// </summary>
    Task ResurrectJobAsync(string jobId, CancellationToken ct = default);


    // ========================================================================
    // 3. DIVINE MODE (Admin Control)
    // ========================================================================

    /// <summary>
    /// [1.4 Divine Mode] Hot Editing for Pending jobs.
    /// </summary>
    Task UpdateJobDataAsync(JobDataUpdateDto update, CancellationToken ct = default);

    /// <summary>
    /// [1.4 Divine Mode] PurgeHistoryAsync (Manual Deletion).
    /// Completely removes a job from ANY table (Hot, Archive, or Morgue).
    /// </summary>
    Task PurgeJobAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Helper: Updates queue settings (Pause/Resume).
    /// </summary>
    ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default);


    // ========================================================================
    // 4. OBSERVABILITY (Dashboard)
    // ========================================================================

    /// <summary>
    /// [1.4 Observability] Hybrid Stats (O(1) counts + queue info).
    /// </summary>
    ValueTask<StatsSummaryEntity?> GetSummaryStatsAsync(string queue, CancellationToken ct = default);

    /// <summary>
    /// [1.4 Observability] GetHistoryJobsAsync.
    /// Paged access to the [JobsSucceeded] table.
    /// </summary>
    ValueTask<IEnumerable<JobSucceededEntity>> GetHistoryJobsAsync(
        string queue,
        int skip,
        int take,
        CancellationToken ct = default);

    /// <summary>
    /// [1.4 Observability] GetMorgueJobsAsync.
    /// Paged access to the [JobsMorgue] table.
    /// </summary>
    ValueTask<IEnumerable<JobMorgueEntity>> GetMorgueJobsAsync(
        string queue,
        int skip,
        int take,
        CancellationToken ct = default);

    // --- Single Item Getters (Needed for details view) ---

    ValueTask<JobEntity?> GetJobEntityAsync(string id, CancellationToken ct = default);
    ValueTask<JobSucceededEntity?> GetSucceededEntityAsync(string id, CancellationToken ct = default);
    ValueTask<JobMorgueEntity?> GetMorgueEntityAsync(string id, CancellationToken ct = default);
    ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default);
    ValueTask<int> MarkZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default);
}