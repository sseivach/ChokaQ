using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Storage.SqlServer.DataEngine;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using ChokaQ.Abstractions.Observability;
using System.Text.Json;

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
    private readonly IChokaQMetrics _metrics;

    public SqlJobStorage(IOptions<SqlJobStorageOptions> options, IChokaQMetrics metrics)
    {
        _options = options.Value;
        _schema = _options.SchemaName;
        _q = new Queries(_schema);
        _metrics = metrics;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private Task ExecuteWithRetryAsync(Func<Task> action, CancellationToken ct) =>
        SqlRetryPolicy.ExecuteAsync(action, _options.MaxTransientRetries, _options.TransientRetryBaseDelayMs, ct);

    private Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct) =>
        SqlRetryPolicy.ExecuteAsync(action, _options.MaxTransientRetries, _options.TransientRetryBaseDelayMs, ct);

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
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);

            // Idempotency check - return existing ID if key exists
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                var existingId = await conn.QueryFirstOrDefaultAsync<string>(
                    _q.CheckIdempotency,
                    new { Key = idempotencyKey }, ct);

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

            _metrics.RecordEnqueue(queue, jobType);

            return id;
        }, ct);
    }

    public async ValueTask<IEnumerable<JobHotEntity>> FetchNextBatchAsync(
        string workerId,
        int batchSize,
        string[]? allowedQueues = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
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
        }, ct);
    }

    public async ValueTask MarkAsProcessingAsync(string jobId, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.MarkAsProcessing, new { Id = jobId }, ct);
        }, ct);
    }

    public async ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.KeepAlive, new { Id = jobId }, ct);
        }, ct);
    }

    public async ValueTask<JobHotEntity?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.QueryFirstOrDefaultAsync<JobHotEntity>(_q.GetJob, new { Id = jobId }, ct);
        }, ct);
    }

    // ========================================================================
    // ATOMIC TRANSITIONS (Three Pillars)
    // ========================================================================

    public async ValueTask ArchiveSucceededAsync(string jobId, double? durationMs = null, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.ArchiveSucceeded, new { Id = jobId, DurationMs = durationMs }, ct);
        }, ct);
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
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.MoveToDLQ, new
            {
                Id = jobId,
                Reason = (int)reason,
                Error = errorDetails ?? "Unknown error (No details provided)"
            }, ct);
        }, ct);
    }

    public async ValueTask ResurrectAsync(
        string jobId,
        JobDataUpdateDto? updates = null,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
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
        }, ct);
    }

    public async ValueTask<int> ResurrectBatchAsync(string[] jobIds, string? resurrectedBy = null, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            if (jobIds.Length == 0) return 0;
            var jsonIds = JsonSerializer.Serialize(jobIds);

            await using var conn = await OpenConnectionAsync(ct);
            return await conn.ExecuteScalarAsync<int>(
                _q.ResurrectBatch,
                new { JsonIds = jsonIds, ResurrectedBy = resurrectedBy },
                ct);
        }, ct);
    }

    public async ValueTask ReleaseJobAsync(string jobId, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.ReleaseJob, new { Id = jobId }, ct);
        }, ct);
    }

    public async ValueTask<int> ArchiveCancelledBatchAsync(string[] jobIds, string? cancelledBy = null, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            if (jobIds.Length == 0) return 0;
            var jsonIds = JsonSerializer.Serialize(jobIds);
            var error = cancelledBy != null ? $"Cancelled by admin: {cancelledBy}" : "Cancelled by admin (Batch)";

            await using var conn = await OpenConnectionAsync(ct);
            return await conn.ExecuteScalarAsync<int>(
                _q.ArchiveCancelledBatch,
                new { JsonIds = jsonIds, Error = error, CancelledBy = cancelledBy },
                ct);
        }, ct);
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
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.RescheduleForRetry, new
            {
                Id = jobId,
                Attempt = newAttemptCount,
                ScheduledAt = scheduledAtUtc
            }, ct);
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
        return await ExecuteWithRetryAsync(async () =>
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
        }, ct);
    }

    public async ValueTask PurgeDLQAsync(string[] jobIds, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            if (jobIds.Length == 0) return;
            var jsonIds = JsonSerializer.Serialize(jobIds);
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.PurgeDLQ, new { JsonIds = jsonIds }, ct);
        }, ct);
    }

    public async ValueTask<int> PurgeArchiveAsync(DateTime olderThan, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.ExecuteAsync(_q.PurgeArchive, new { CutOff = olderThan }, ct);
        }, ct);
    }

    public async ValueTask<bool> UpdateDLQJobDataAsync(
        string jobId,
        JobDataUpdateDto updates,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            if (updates.Payload == null && updates.Tags == null) return false;

            await using var conn = await OpenConnectionAsync(ct);
            var affected = await conn.ExecuteAsync(_q.UpdateDLQData, new
            {
                Id = jobId,
                updates.Payload,
                updates.Tags,
                ModifiedBy = modifiedBy
            }, ct);

            return affected > 0;
        }, ct);
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
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var where = "WHERE 1=1";
        if (!string.IsNullOrEmpty(queueFilter)) where += " AND [Queue] = @Queue";
        if (reasonFilter.HasValue) where += " AND [FailureReason] = @Reason";
        if (fromDate.HasValue) where += " AND [CreatedAtUtc] >= @FromDate";
        if (toDate.HasValue) where += " AND [CreatedAtUtc] <= @ToDate";
        if (!string.IsNullOrEmpty(searchTerm))
            where += " AND ([Id] LIKE @Search OR [Type] LIKE @Search OR [Tags] LIKE @Search OR [ErrorDetails] LIKE @Search)";

        var sql = _q.GetDLQJobs.Replace("{WHERE_CLAUSE}", where);

        return await conn.QueryAsync<JobDLQEntity>(sql, new
        {
            Limit = limit,
            Queue = queueFilter,
            Reason = reasonFilter.HasValue ? (int)reasonFilter.Value : (int?)null,
            FromDate = fromDate,
            ToDate = toDate,
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
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.QueryAsync<QueueEntity>(_q.GetQueues, null, ct);
        }, ct);
    }

    public async ValueTask SetQueuePausedAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.SetQueuePaused, new { Name = queueName, IsPaused = isPaused }, ct);
        }, ct);
    }

    public async ValueTask SetQueueZombieTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.SetQueueZombieTimeout, new { Name = queueName, Timeout = timeoutSeconds }, ct);
        }, ct);
    }

    public async ValueTask SetQueueActiveAsync(string queueName, bool isActive, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.SetQueueActive, new { Name = queueName, IsActive = isActive }, ct);
        }, ct);
    }

    // ========================================================================
    // ZOMBIE DETECTION
    // ========================================================================

    public async ValueTask<int> ArchiveZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.ExecuteScalarAsync<int>(_q.ArchiveZombies, new { GlobalTimeout = globalTimeoutSeconds }, ct);
        }, ct);
    }

    // ========================================================================
    // HISTORY
    // ========================================================================

    public async ValueTask<PagedResult<JobArchiveEntity>> GetArchivePagedAsync(
        HistoryFilterDto filter,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var (whereSql, parameters) = BuildFilterSql(filter, isArchive: true);

        var countSql = _q.GetArchiveCount.Replace("{WHERE_CLAUSE}", whereSql);
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters, ct);

        if (totalCount == 0)
            return PagedResult<JobArchiveEntity>.Empty(filter.PageSize);

        var orderBy = SqlSortBuilder.BuildOrderBy(filter.SortBy, filter.SortDescending, isArchive: true);

        var dataSql = _q.GetArchivePaged
            .Replace("{WHERE_CLAUSE}", whereSql)
            .Replace("{ORDER_BY}", orderBy);

        var combinedParams = MergeParams(parameters, new
        {
            Offset = (filter.PageNumber - 1) * filter.PageSize,
            Limit = filter.PageSize
        });

        var items = await conn.QueryAsync<JobArchiveEntity>(dataSql, combinedParams, ct);

        return new PagedResult<JobArchiveEntity>(items, totalCount, filter.PageNumber, filter.PageSize);
    }

    public async ValueTask<PagedResult<JobDLQEntity>> GetDLQPagedAsync(
        HistoryFilterDto filter,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var (whereSql, parameters) = BuildFilterSql(filter, isArchive: false);

        var countSql = _q.GetDLQCount.Replace("{WHERE_CLAUSE}", whereSql);
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters, ct);

        if (totalCount == 0)
            return PagedResult<JobDLQEntity>.Empty(filter.PageSize);

        var orderBy = SqlSortBuilder.BuildOrderBy(filter.SortBy, filter.SortDescending, isArchive: false);

        var dataSql = _q.GetDLQPaged
            .Replace("{WHERE_CLAUSE}", whereSql)
            .Replace("{ORDER_BY}", orderBy);

        var combinedParams = MergeParams(parameters, new
        {
            Offset = (filter.PageNumber - 1) * filter.PageSize,
            Limit = filter.PageSize
        });

        var items = await conn.QueryAsync<JobDLQEntity>(dataSql, combinedParams, ct);

        return new PagedResult<JobDLQEntity>(items, totalCount, filter.PageNumber, filter.PageSize);
    }

    // --- Private Helpers ---

    private (string Sql, Dictionary<string, object?> Params) BuildFilterSql(HistoryFilterDto filter, bool isArchive)
    {
        var sb = new System.Text.StringBuilder("WHERE 1=1");
        var p = new Dictionary<string, object?>();

        // Date Range
        if (filter.FromUtc.HasValue)
        {
            var col = isArchive ? "[FinishedAtUtc]" : "[CreatedAtUtc]"; // Or FailedAtUtc depending on requirement
            sb.Append($" AND {col} >= @FromUtc");
            p["FromUtc"] = filter.FromUtc.Value;
        }
        if (filter.ToUtc.HasValue)
        {
            var col = isArchive ? "[FinishedAtUtc]" : "[CreatedAtUtc]";
            sb.Append($" AND {col} <= @ToUtc");
            p["ToUtc"] = filter.ToUtc.Value;
        }

        // Queue
        if (!string.IsNullOrEmpty(filter.Queue))
        {
            sb.Append(" AND [Queue] = @Queue");
            p["Queue"] = filter.Queue;
        }

        // Search Term (Expensive LIKE)
        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            sb.Append(" AND ([Id] LIKE @Search OR [Type] LIKE @Search OR [Tags] LIKE @Search)");
            p["Search"] = $"%{filter.SearchTerm}%";
        }

        return (sb.ToString(), p);
    }

    // Helper to merge two anonymous objects or dictionaries into one dictionary
    private Dictionary<string, object?> MergeParams(Dictionary<string, object?> first, object second)
    {
        var result = new Dictionary<string, object?>(first);
        foreach (var prop in second.GetType().GetProperties())
        {
            result[prop.Name] = prop.GetValue(second);
        }
        return result;
    }
}