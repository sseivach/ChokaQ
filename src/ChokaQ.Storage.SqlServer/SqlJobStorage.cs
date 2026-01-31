using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Storage.SqlServer.DataEngine;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// SQL Server implementation of IJobStorage using Three Pillars architecture.
/// 
/// Tables:
/// - JobsHot: Active jobs (Pending, Fetched, Processing)
/// - JobsArchive: Succeeded jobs (History)
/// - JobsDLQ: Failed/Cancelled/Zombie jobs (Dead Letter Queue)
/// - StatsSummary: Pre-aggregated counters
/// - Queues: Queue configuration
/// </summary>
public class SqlJobStorage : IJobStorage
{
    private readonly SqlJobStorageOptions _options;
    private readonly string _schema;
    private readonly Queries _q;

    public SqlJobStorage(IOptions<SqlJobStorageOptions> options)
    {
        _options = options.Value;
        _schema = _options.SchemaName;
        _q = new Queries(_schema);
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ========================================================================
    // CORE OPERATIONS (Hot Table)
    // ========================================================================

    public async ValueTask<string> EnqueueAsync(
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
        await using var conn = await OpenConnectionAsync(ct);

        // Idempotency check - return existing ID if key exists
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existingId = await conn.QueryFirstOrDefaultAsync<string>(
                _q.CheckIdempotency,
                new { Key = idempotencyKey });

            if (existingId != null)
                return existingId;
        }

        await conn.ExecuteAsync(_q.EnqueueJob, new
        {
            Id = id,
            Queue = queue,
            Type = jobType,
            Payload = payload,
            Tags = tags,
            IdempotencyKey = idempotencyKey,
            Priority = priority,
            CreatedBy = createdBy,
            ScheduledAt = delay.HasValue ? DateTime.UtcNow.Add(delay.Value) : (DateTime?)null
        }, ct);

        return id;
    }

    public async ValueTask<IEnumerable<JobHotEntity>> FetchNextBatchAsync(
        string workerId,
        int batchSize,
        string[]? allowedQueues = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // CTE + UPDLOCK + READPAST = atomic lock acquisition without race conditions
        var queueFilter = allowedQueues?.Length > 0
            ? "AND h.[Queue] IN @Queues"
            : "";

        var sql = _q.FetchNextBatch.Replace("{QUEUE_FILTER}", queueFilter);

        return await conn.QueryAsync<JobHotEntity>(
            sql,
            new { Limit = batchSize, WorkerId = workerId, Queues = allowedQueues },
            ct);
    }

    public async ValueTask MarkAsProcessingAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(_q.MarkAsProcessing, new { Id = jobId }, ct);
    }

    public async ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(_q.KeepAlive, new { Id = jobId }, ct);
    }

    public async ValueTask<JobHotEntity?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<JobHotEntity>(_q.GetJob, new { Id = jobId }, ct);
    }
    // ========================================================================
    // ATOMIC TRANSITIONS (Three Pillars)
    // ========================================================================

    public async ValueTask ArchiveSucceededAsync(string jobId, double? durationMs = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(_q.ArchiveSucceeded, new { Id = jobId, DurationMs = durationMs }, ct);
    }

    public async ValueTask ArchiveFailedAsync(string jobId, string errorDetails, CancellationToken ct = default)
    {
        await MoveToDLQAsync(jobId, FailureReason.MaxRetriesExceeded, errorDetails, ct);
    }

    public async ValueTask ArchiveCancelledAsync(string jobId, string? cancelledBy = null, CancellationToken ct = default)
    {
        var error = cancelledBy != null ? $"Cancelled by: {cancelledBy}" : "Cancelled by admin";
        await MoveToDLQAsync(jobId, FailureReason.Cancelled, error, ct);
    }

    public async ValueTask ArchiveZombieAsync(string jobId, CancellationToken ct = default)
    {
        await MoveToDLQAsync(jobId, FailureReason.Zombie, "Zombie: Worker heartbeat expired", ct);
    }

    private async ValueTask MoveToDLQAsync(string jobId, FailureReason reason, string errorDetails, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(_q.MoveToDLQ, new
        {
            Id = jobId,
            Reason = (int)reason,
            Error = errorDetails ?? "Unknown error (No details provided)"
        }, ct);
    }

    public async ValueTask ResurrectAsync(
        string jobId,
        JobDataUpdateDto? updates = null,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(_q.Resurrect, new
        {
            Id = jobId,
            NewPayload = updates?.Payload,
            NewTags = updates?.Tags,
            NewPriority = updates?.Priority,
            ResurrectedBy = resurrectedBy
        }, ct);
    }

    public async ValueTask<int> ResurrectBatchAsync(string[] jobIds, string? resurrectedBy = null, CancellationToken ct = default)
    {
        if (jobIds.Length == 0) return 0;

        await using var conn = await OpenConnectionAsync(ct);
        int total = 0;

        // Process in batches of 1000
        foreach (var batch in jobIds.Chunk(1000))
        {
            var affected = await conn.ExecuteAsync(
                _q.ResurrectBatch, 
                new { Ids = batch, ResurrectedBy = resurrectedBy }, 
                ct);
            total += affected / 2; // Each job = 1 INSERT + 1 DELETE
        }

        return total;
    }

    // ========================================================================
    // RETRY LOGIC (Stays in Hot)
    // ========================================================================

    public async ValueTask RescheduleForRetryAsync(
        string jobId,
        DateTime scheduledAtUtc,
        int newAttemptCount,
        string lastError,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(_q.RescheduleForRetry, new
        {
            Id = jobId,
            Attempt = newAttemptCount,
            ScheduledAt = scheduledAtUtc
        }, ct);
    }

    // ========================================================================
    // DIVINE MODE (Admin Operations)
    // ========================================================================

    public async ValueTask<bool> UpdateJobDataAsync(
        string jobId,
        JobDataUpdateDto updates,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        if (!updates.HasChanges) return false;

        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(_q.UpdateJobData, new
        {
            Id = jobId,
            updates.Payload,
            updates.Tags,
            updates.Priority,
            ModifiedBy = modifiedBy
        }, ct);

        return affected > 0;
    }

    public async ValueTask PurgeDLQAsync(string[] jobIds, CancellationToken ct = default)
    {
        if (jobIds.Length == 0) return;

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(_q.PurgeDLQ, new { Ids = jobIds }, ct);
    }

    public async ValueTask<int> PurgeArchiveAsync(DateTime olderThan, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(_q.PurgeArchive, new { CutOff = olderThan }, ct);
    }

    // ========================================================================
    // OBSERVABILITY (Dashboard)
    // ========================================================================

    public async ValueTask<StatsSummaryEntity> GetSummaryStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<StatsSummaryEntity>(_q.GetSummaryStats, null, ct);
    }

    public async ValueTask<IEnumerable<StatsSummaryEntity>> GetQueueStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QueryAsync<StatsSummaryEntity>(_q.GetQueueStats, null, ct);
    }

    public async ValueTask<IEnumerable<JobHotEntity>> GetActiveJobsAsync(
        int limit = 100,
        JobStatus? statusFilter = null,
        string? queueFilter = null,
        string? searchTerm = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var where = "WHERE 1=1";
        if (statusFilter.HasValue) where += " AND [Status] = @Status";
        if (!string.IsNullOrEmpty(queueFilter)) where += " AND [Queue] = @Queue";
        if (!string.IsNullOrEmpty(searchTerm))
            where += " AND ([Id] LIKE @Search OR [Type] LIKE @Search OR [Tags] LIKE @Search)";

        var sql = _q.GetActiveJobs.Replace("{WHERE_CLAUSE}", where);

        return await conn.QueryAsync<JobHotEntity>(sql, new
        {
            Limit = limit,
            Status = statusFilter.HasValue ? (int)statusFilter.Value : (int?)null,
            Queue = queueFilter,
            Search = $"%{searchTerm}%"
        }, ct);
    }

    public async ValueTask<IEnumerable<JobArchiveEntity>> GetArchiveJobsAsync(
        int limit = 100,
        string? queueFilter = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? tagFilter = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var where = "WHERE 1=1";
        if (!string.IsNullOrEmpty(queueFilter)) where += " AND [Queue] = @Queue";
        if (fromDate.HasValue) where += " AND [FinishedAtUtc] >= @FromDate";
        if (toDate.HasValue) where += " AND [FinishedAtUtc] <= @ToDate";
        if (!string.IsNullOrEmpty(tagFilter)) where += " AND [Tags] LIKE @TagFilter";

        var sql = _q.GetArchiveJobs.Replace("{WHERE_CLAUSE}", where);

        return await conn.QueryAsync<JobArchiveEntity>(sql, new
        {
            Limit = limit,
            Queue = queueFilter,
            FromDate = fromDate,
            ToDate = toDate,
            TagFilter = $"%{tagFilter}%"
        }, ct);
    }

    public async ValueTask<IEnumerable<JobDLQEntity>> GetDLQJobsAsync(
        int limit = 100,
        string? queueFilter = null,
        FailureReason? reasonFilter = null,
        string? searchTerm = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var where = "WHERE 1=1";
        if (!string.IsNullOrEmpty(queueFilter)) where += " AND [Queue] = @Queue";
        if (reasonFilter.HasValue) where += " AND [FailureReason] = @Reason";
        if (!string.IsNullOrEmpty(searchTerm))
            where += " AND ([Id] LIKE @Search OR [Type] LIKE @Search OR [Tags] LIKE @Search OR [ErrorDetails] LIKE @Search)";

        var sql = _q.GetDLQJobs.Replace("{WHERE_CLAUSE}", where);

        return await conn.QueryAsync<JobDLQEntity>(sql, new
        {
            Limit = limit,
            Queue = queueFilter,
            Reason = reasonFilter.HasValue ? (int)reasonFilter.Value : (int?)null,
            Search = $"%{searchTerm}%"
        }, ct);
    }

    public async ValueTask<JobArchiveEntity?> GetArchiveJobAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<JobArchiveEntity>(_q.GetArchiveJob, new { Id = jobId }, ct);
    }

    public async ValueTask<JobDLQEntity?> GetDLQJobAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<JobDLQEntity>(_q.GetDLQJob, new { Id = jobId }, ct);
    }

    // ========================================================================
    // QUEUE MANAGEMENT
    // ========================================================================

    public async ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QueryAsync<QueueEntity>(_q.GetQueues, null, ct);
    }

    public async ValueTask SetQueuePausedAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(_q.SetQueuePaused, new { Name = queueName, IsPaused = isPaused }, ct);
    }

    public async ValueTask SetQueueZombieTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(_q.SetQueueZombieTimeout, new { Name = queueName, Timeout = timeoutSeconds }, ct);
    }

    // ========================================================================
    // ZOMBIE DETECTION
    // ========================================================================

    public async ValueTask<int> ArchiveZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(_q.ArchiveZombies, new { GlobalTimeout = globalTimeoutSeconds }, ct);
    }
}
