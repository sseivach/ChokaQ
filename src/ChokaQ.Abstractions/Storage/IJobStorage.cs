using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;

namespace ChokaQ.Abstractions;

public interface IJobStorage
{
    // ========================================================================
    // 1. CORE (Worker & Producer)
    // ========================================================================

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

    ValueTask<IEnumerable<JobEntity>> FetchNextBatchAsync(
        string workerId,
        int limit,
        string[]? allowedQueues,
        CancellationToken ct = default);

    /// <summary>
    /// [MISSING LINK] Transitions job from Fetched (1) to Processing (2).
    /// Updates LockedAtUtc to prevent zombie cleanup during execution.
    /// </summary>
    Task MarkAsProcessingAsync(string jobId, CancellationToken ct = default);

    ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default);


    // ========================================================================
    // 2. TRANSITIONS (Atomic Move)
    // ========================================================================

    Task RetryJobAsync(string jobId, int nextAttempt, TimeSpan delay, string? lastError, CancellationToken ct = default);

    Task ArchiveAsSuccessAsync(JobSucceededEntity archiveRecord, CancellationToken ct = default);

    Task ArchiveAsMorgueAsync(JobMorgueEntity morgueRecord, CancellationToken ct = default);

    Task ResurrectJobAsync(string jobId, CancellationToken ct = default);


    // ========================================================================
    // 3. DIVINE MODE (Admin Control / Hub)
    // ========================================================================

    Task UpdateJobDataAsync(JobDataUpdateDto update, CancellationToken ct = default);

    Task PurgeJobAsync(string id, CancellationToken ct = default);

    ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default);

    Task UpdateJobPriorityAsync(string jobId, int priority, CancellationToken ct = default);

    Task UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default);


    // ========================================================================
    // 4. OBSERVABILITY (Dashboard)
    // ========================================================================

    /// <summary>
    /// Returns aggregated counts for top-level stats cards.
    /// </summary>
    ValueTask<JobCountsDto> GetJobCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns queues with aggregated real-time counters (Pending, Processing, etc.)
    /// </summary>
    ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default);

    ValueTask<IEnumerable<JobEntity>> GetActiveJobsAsync(string queue, int skip, int take, CancellationToken ct = default);

    ValueTask<IEnumerable<JobSucceededEntity>> GetHistoryJobsAsync(string queue, int skip, int take, CancellationToken ct = default);

    ValueTask<IEnumerable<JobMorgueEntity>> GetMorgueJobsAsync(string queue, int skip, int take, CancellationToken ct = default);

    // --- Single Entity Lookups ---

    ValueTask<JobEntity?> GetJobEntityAsync(string id, CancellationToken ct = default);
    ValueTask<JobSucceededEntity?> GetSucceededEntityAsync(string id, CancellationToken ct = default);
    ValueTask<JobMorgueEntity?> GetMorgueEntityAsync(string id, CancellationToken ct = default);

    // --- Maintenance ---

    ValueTask<int> MarkZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default);
}