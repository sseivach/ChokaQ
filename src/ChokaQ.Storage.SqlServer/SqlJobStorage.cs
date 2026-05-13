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
/// /// <summary>
/// SQL Server implementation of IJobStorage using Three Pillars architecture.
/// 
/// Tables:
/// - JobsHot: Active jobs (Pending, Fetched, Processing)
/// - JobsArchive: Succeeded jobs (History)
/// - JobsDLQ: Failed/Cancelled/Zombie jobs (Dead Letter Queue)
/// - StatsSummary: Lifetime counters
/// - MetricBuckets: Rolling throughput and failure-rate aggregates
/// - Queues: Queue configuration
/// All database calls are wrapped in a resilient transient fault handling policy.
/// </summary>
public class SqlJobStorage : IJobStorage
{
    private const int TopErrorLimit = 5;
    private const int TopErrorSampleSize = 5000;
    private const int ErrorPrefixLength = 160;

    private readonly SqlJobStorageOptions _options;
    private readonly string _schema;
    private readonly Queries _q;
    private readonly IChokaQMetrics? _metrics;

    public SqlJobStorage(IOptions<SqlJobStorageOptions> options, IChokaQMetrics? metrics = null)
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

        // Command timeout is applied inside SqlMapper because every storage operation creates
        // its own SqlCommand there. Stamping the open connection keeps timeout policy centralized:
        // workers, dashboard queries, recovery scans, and admin operations all share the same
        // fail-fast database boundary unless a future feature deliberately adds per-command tiers.
        conn.SetCommandTimeout(_options.CommandTimeoutSeconds);

        return conn;
    }

    private Task ExecuteWithRetryAsync(Func<Task> action, CancellationToken ct) =>
        SqlRetryPolicy.ExecuteAsync(action, _options.MaxTransientRetries, _options.TransientRetryBaseDelayMs, ct);

    private Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct) =>
        SqlRetryPolicy.ExecuteAsync(action, _options.MaxTransientRetries, _options.TransientRetryBaseDelayMs, ct);

    private int CleanupBatchSize => Math.Max(1, _options.CleanupBatchSize);

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
        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey);

        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);

            if (!string.IsNullOrEmpty(normalizedIdempotencyKey))
            {
                // Built-in enqueue idempotency is a Hot-table admission guard. It prevents two
                // active jobs with the same business key, but it deliberately does not scan Archive
                // or DLQ. Historical tables are for audit and operator recovery, not for rejecting
                // future logical attempts after the original work has completed or failed.
                var existingId = await conn.QueryFirstOrDefaultAsync<string>(
                    _q.CheckIdempotency,
                    new { Key = normalizedIdempotencyKey }, ct);

                if (existingId != null)
                    return existingId;
            }

            try
            {
                await conn.ExecuteAsync(_q.EnqueueJob, new
                {
                    Id = id,
                    Queue = queue,
                    Type = jobType,
                    Payload = payload,
                    Tags = tags,
                    IdempotencyKey = normalizedIdempotencyKey,
                    Priority = priority,
                    CreatedBy = createdBy,
                    ScheduledAt = delay.HasValue ? DateTime.UtcNow.Add(delay.Value) : (DateTime?)null
                }, ct);

                _metrics?.RecordEnqueue(queue, jobType);

                return id;
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
            {
                if (!string.IsNullOrEmpty(normalizedIdempotencyKey))
                {
                    var winnerId = await conn.QueryFirstOrDefaultAsync<string>(
                        _q.CheckIdempotency,
                        new { Key = normalizedIdempotencyKey }, ct);

                    if (winnerId != null)
                        return winnerId;
                }
                throw;
            }
        }, ct);
    }

    private static string? NormalizeIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return null;

        var normalized = idempotencyKey.Trim();
        if (normalized.Length > 255)
            throw new ArgumentException("IdempotencyKey exceeds maximum length of 255 characters.", nameof(idempotencyKey));

        return normalized;
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

    public async ValueTask<bool> MarkAsProcessingAsync(
        string jobId,
        CancellationToken ct = default,
        string? workerId = null)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            var moved = await conn.ExecuteScalarAsync<int>(
                _q.MarkAsProcessing,
                new { Id = jobId, WorkerId = workerId },
                ct);
            return moved > 0;
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

    public async ValueTask<bool> ArchiveSucceededAsync(
        string jobId,
        double? durationMs = null,
        CancellationToken ct = default,
        string? workerId = null)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            var moved = await conn.ExecuteScalarAsync<int>(
                _q.ArchiveSucceeded,
                new { Id = jobId, DurationMs = durationMs, WorkerId = workerId },
                ct);
            return moved > 0;
        }, ct);
    }

    public async ValueTask<bool> ArchiveFailedAsync(
        string jobId,
        string errorDetails,
        CancellationToken ct = default,
        string? workerId = null,
        FailureReason failureReason = FailureReason.MaxRetriesExceeded)
    {
        // FailureReason is persisted as data because the DLQ is an operator workflow, not just
        // an exception dump. Free-form stack traces are for debugging; taxonomy is for routing,
        // filtering, dashboards, and incident triage.
        return await MoveToDLQAsync(jobId, failureReason, errorDetails, ct, workerId);
    }

    public async ValueTask<bool> ArchiveCancelledAsync(
        string jobId,
        string? cancelledBy = null,
        CancellationToken ct = default,
        string? workerId = null)
    {
        var error = cancelledBy != null ? $"Cancelled by: {cancelledBy}" : "Cancelled by admin";
        return await MoveToDLQAsync(jobId, FailureReason.Cancelled, error, ct, workerId);
    }

    public async ValueTask<bool> ArchiveZombieAsync(
        string jobId,
        CancellationToken ct = default,
        string? workerId = null)
    {
        return await MoveToDLQAsync(jobId, FailureReason.Zombie, "Zombie: Worker heartbeat expired", ct, workerId);
    }

    private async ValueTask<bool> MoveToDLQAsync(
        string jobId,
        FailureReason reason,
        string errorDetails,
        CancellationToken ct,
        string? workerId)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            var moved = await conn.ExecuteScalarAsync<int>(_q.MoveToDLQ, new
            {
                Id = jobId,
                Reason = (int)reason,
                Error = errorDetails ?? "Unknown error (No details provided)",
                WorkerId = workerId
            }, ct);
            return moved > 0;
        }, ct);
    }

    public async ValueTask ResurrectAsync(
        string jobId,
        JobDataUpdateDto? updates = null,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        _ = await RepairAndRequeueDLQCoreAsync(jobId, updates, resurrectedBy, ct);
    }

    public async ValueTask<bool> RepairAndRequeueDLQAsync(
        string jobId,
        JobDataUpdateDto updates,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        // The editor uses this return value to avoid pretending a repair succeeded after another
        // operator already purged or requeued the same DLQ row. The SQL transaction below still
        // owns the real safety guarantee; the bool is the UI contract on top of it.
        return await RepairAndRequeueDLQCoreAsync(jobId, updates, resurrectedBy, ct);
    }

    private async ValueTask<bool> RepairAndRequeueDLQCoreAsync(
        string jobId,
        JobDataUpdateDto? updates,
        string? resurrectedBy,
        CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            var moved = await conn.ExecuteScalarAsync<int>(_q.Resurrect, new
            {
                Id = jobId,
                NewPayload = updates?.Payload,
                NewTags = updates?.Tags,
                NewPriority = updates?.Priority,
                ResurrectedBy = resurrectedBy
            }, ct);
            return moved > 0;
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

    public async ValueTask<bool> RescheduleForRetryAsync(
        string jobId,
        DateTime scheduledAtUtc,
        int newAttemptCount,
        string lastError,
        CancellationToken ct = default,
        string? workerId = null)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            var moved = await conn.ExecuteScalarAsync<int>(_q.RescheduleForRetry, new
            {
                Id = jobId,
                Attempt = newAttemptCount,
                ScheduledAt = scheduledAtUtc,
                WorkerId = workerId
            }, ct);
            return moved > 0;
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
        if (jobIds.Length == 0) return;

        var jsonIds = JsonSerializer.Serialize(jobIds);
        var totalDeleted = 0;
        var batchSize = CleanupBatchSize;

        while (totalDeleted < jobIds.Length)
        {
            ct.ThrowIfCancellationRequested();

            // Retention-style deletes are retried one short transaction at a time. Retrying the
            // whole multi-batch loop after a transient SQL error would hide how much work already
            // committed; per-batch retry keeps the mutation idempotent and the transaction log calm.
            var deleted = await ExecuteWithRetryAsync(async () =>
            {
                await using var conn = await OpenConnectionAsync(ct);
                return await conn.ExecuteScalarAsync<int>(
                    _q.PurgeDLQ,
                    new { JsonIds = jsonIds, BatchSize = batchSize },
                    ct);
            }, ct);

            if (deleted <= 0)
                break;

            totalDeleted += deleted;

            if (deleted < batchSize)
                break;
        }
    }

    public async ValueTask<DlqBulkOperationPreviewDto> PreviewDLQBulkOperationAsync(
        DlqBulkOperationFilterDto filter,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var limit = NormalizeDlqBulkLimit(filter);
            var (whereSql, parameters) = BuildDlqBulkFilterSql(filter);

            await using var conn = await OpenConnectionAsync(ct);

            var countSql = _q.GetDLQCount.Replace("{WHERE_CLAUSE}", whereSql);
            var matchedCount = await conn.ExecuteScalarAsync<int>(countSql, parameters, ct);

            var sampleSql = _q.GetDLQBulkIds.Replace("{WHERE_CLAUSE}", whereSql);
            var sampleParams = MergeParams(parameters, new { MaxJobs = Math.Min(limit, 10) });
            var sampleIds = (await conn.QueryAsync<string>(sampleSql, sampleParams, ct)).ToArray();

            return new DlqBulkOperationPreviewDto(matchedCount, limit, sampleIds);
        }, ct);
    }

    public async ValueTask<int> PurgeDLQByFilterAsync(
        DlqBulkOperationFilterDto filter,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var limit = NormalizeDlqBulkLimit(filter);
            var (whereSql, parameters) = BuildDlqBulkFilterSql(filter);
            var sql = _q.PurgeDLQByFilter.Replace("{WHERE_CLAUSE}", whereSql);
            var combinedParams = MergeParams(parameters, new { MaxJobs = limit });

            await using var conn = await OpenConnectionAsync(ct);
            return await conn.ExecuteScalarAsync<int>(sql, combinedParams, ct);
        }, ct);
    }

    public async ValueTask<int> ResurrectDLQByFilterAsync(
        DlqBulkOperationFilterDto filter,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var limit = NormalizeDlqBulkLimit(filter);
            var (whereSql, parameters) = BuildDlqBulkFilterSql(filter);
            var sql = _q.ResurrectDLQByFilter.Replace("{WHERE_CLAUSE}", whereSql);
            var combinedParams = MergeParams(parameters, new
            {
                MaxJobs = limit,
                ResurrectedBy = resurrectedBy
            });

            await using var conn = await OpenConnectionAsync(ct);
            return await conn.ExecuteScalarAsync<int>(sql, combinedParams, ct);
        }, ct);
    }

    public async ValueTask<int> PurgeArchiveAsync(DateTime olderThan, CancellationToken ct = default)
    {
        var totalDeleted = 0;
        var batchSize = CleanupBatchSize;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Archive cleanup may remove months of history. Running it as many small commits is
            // slower in the happy path, but it is much safer for shared SQL Server instances:
            // locks are released quickly and transaction-log growth stays bounded.
            var deleted = await ExecuteWithRetryAsync(async () =>
            {
                await using var conn = await OpenConnectionAsync(ct);
                return await conn.ExecuteScalarAsync<int>(
                    _q.PurgeArchive,
                    new { CutOff = olderThan, BatchSize = batchSize },
                    ct);
            }, ct);

            if (deleted <= 0)
                break;

            totalDeleted += deleted;

            if (deleted < batchSize)
                break;
        }

        return totalDeleted;
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
    // OBSERVABILITY (Dashboard) - NOW PROTECTED WITH RETRIES
    // ========================================================================

    public async ValueTask<StatsSummaryEntity> GetSummaryStatsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.QuerySingleAsync<StatsSummaryEntity>(_q.GetSummaryStats, null, ct);
        }, ct);
    }

    public async ValueTask<IEnumerable<StatsSummaryEntity>> GetQueueStatsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.QueryAsync<StatsSummaryEntity>(_q.GetQueueStats, null, ct);
        }, ct);
    }

    public async ValueTask<SystemHealthDto> GetSystemHealthAsync(CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var oneMinuteCutoff = now.AddMinutes(-1);
            var fiveMinuteCutoff = now.AddMinutes(-5);

            await using var conn = await OpenConnectionAsync(ct);

            // Keep the health snapshot composed inside storage. The dashboard should not know
            // which tables supply saturation, throughput, and DLQ triage data; that knowledge
            // belongs to the persistence implementation and its indexes.
            var queueRows = (await conn.QueryAsync<QueueHealthRow>(_q.GetQueueHealth, null, ct)).ToList();
            var throughput = await conn.QuerySingleAsync<ThroughputHealthRow>(_q.GetThroughputStats, new
            {
                OneMinuteCutoff = oneMinuteCutoff,
                FiveMinuteCutoff = fiveMinuteCutoff
            }, ct);
            var topErrorRows = (await conn.QueryAsync<DlqErrorGroupRow>(_q.GetTopDlqErrors, new
            {
                TopErrorLimit,
                TopErrorSampleSize,
                ErrorPrefixLength
            }, ct)).ToList();

            return new SystemHealthDto(
                GeneratedAtUtc: now,
                Queues: queueRows
                    .Select(q => new QueueHealthDto(q.Queue, q.Pending, q.AverageLagSeconds, q.MaxLagSeconds))
                    .ToList(),
                JobsPerSecondLastMinute: CalculateJobsPerSecond(throughput.ProcessedLastMinute, 60),
                JobsPerSecondLastFiveMinutes: CalculateJobsPerSecond(throughput.ProcessedLastFiveMinutes, 300),
                FailureRateLastMinutePercent: CalculateFailureRatePercent(throughput.FailedLastMinute, throughput.ProcessedLastMinute),
                FailureRateLastFiveMinutesPercent: CalculateFailureRatePercent(throughput.FailedLastFiveMinutes, throughput.ProcessedLastFiveMinutes),
                TopErrors: topErrorRows
                    .Select(e => new DlqErrorGroupDto(e.FailureReason, e.ErrorPrefix, e.Count, e.LatestFailedAtUtc))
                    .ToList());
        }, ct);
    }

    public async ValueTask<IEnumerable<JobHotEntity>> GetActiveJobsAsync(
        int limit = 100,
        JobStatus? statusFilter = null,
        string? queueFilter = null,
        string? searchTerm = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
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
        return await ExecuteWithRetryAsync(async () =>
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
        return await ExecuteWithRetryAsync(async () =>
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
        }, ct);
    }

    public async ValueTask<JobArchiveEntity?> GetArchiveJobAsync(string jobId, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.QueryFirstOrDefaultAsync<JobArchiveEntity>(_q.GetArchiveJob, new { Id = jobId }, ct);
        }, ct);
    }

    public async ValueTask<JobDLQEntity?> GetDLQJobAsync(string jobId, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.QueryFirstOrDefaultAsync<JobDLQEntity>(_q.GetDLQJob, new { Id = jobId }, ct);
        }, ct);
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

    public async ValueTask SetQueueMaxWorkersAsync(string queueName, int? maxWorkers, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(_q.SetQueueMaxWorkers, new { Name = queueName, MaxWorkers = maxWorkers }, ct);
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
    // RECOVERY & ZOMBIE DETECTION
    // ========================================================================

    public async ValueTask<int> RecoverAbandonedAsync(int timeoutSeconds, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.ExecuteAsync(_q.RecoverAbandoned, new { TimeoutSeconds = timeoutSeconds }, ct);
        }, ct);
    }

    public async ValueTask<int> ArchiveZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.ExecuteScalarAsync<int>(_q.ArchiveZombies, new { GlobalTimeout = globalTimeoutSeconds }, ct);
        }, ct);
    }

    // ========================================================================
    // HISTORY - NOW PROTECTED WITH RETRIES
    // ========================================================================

    public async ValueTask<PagedResult<JobArchiveEntity>> GetArchivePagedAsync(
        HistoryFilterDto filter,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
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
        }, ct);
    }

    public async ValueTask<PagedResult<JobDLQEntity>> GetDLQPagedAsync(
        HistoryFilterDto filter,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
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
        }, ct);
    }

    // --- Private Helpers ---

    private static int NormalizeDlqBulkLimit(DlqBulkOperationFilterDto filter)
    {
        var requested = filter.MaxJobs <= 0
            ? DlqBulkOperationFilterDto.DefaultMaxJobs
            : filter.MaxJobs;

        return Math.Clamp(requested, 1, DlqBulkOperationFilterDto.AbsoluteMaxJobs);
    }

    private (string Sql, Dictionary<string, object?> Params) BuildDlqBulkFilterSql(DlqBulkOperationFilterDto filter)
    {
        var sb = new System.Text.StringBuilder("WHERE 1=1");
        var p = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(filter.Queue))
        {
            sb.Append(" AND [Queue] = @BulkQueue");
            p["BulkQueue"] = filter.Queue.Trim();
        }

        if (filter.FailureReason.HasValue)
        {
            sb.Append(" AND [FailureReason] = @BulkFailureReason");
            p["BulkFailureReason"] = (int)filter.FailureReason.Value;
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            sb.Append(" AND [Type] = @BulkType");
            p["BulkType"] = filter.Type.Trim();
        }

        if (filter.FromUtc.HasValue)
        {
            sb.Append(" AND [CreatedAtUtc] >= @BulkFromUtc");
            p["BulkFromUtc"] = filter.FromUtc.Value;
        }

        if (filter.ToUtc.HasValue)
        {
            sb.Append(" AND [CreatedAtUtc] <= @BulkToUtc");
            p["BulkToUtc"] = filter.ToUtc.Value;
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            // Search stays parameterized even though it supports wildcards. That keeps the operator
            // experience flexible without turning a dashboard text box into raw SQL surface area.
            sb.Append(" AND ([Id] LIKE @BulkSearch OR [Type] LIKE @BulkSearch OR [Tags] LIKE @BulkSearch OR [ErrorDetails] LIKE @BulkSearch)");
            p["BulkSearch"] = $"%{filter.SearchTerm.Trim()}%";
        }

        return (sb.ToString(), p);
    }

    private (string Sql, Dictionary<string, object?> Params) BuildFilterSql(HistoryFilterDto filter, bool isArchive)
    {
        var sb = new System.Text.StringBuilder("WHERE 1=1");
        var p = new Dictionary<string, object?>();

        if (filter.FromUtc.HasValue)
        {
            var col = isArchive ? "[FinishedAtUtc]" : "[CreatedAtUtc]";
            sb.Append($" AND {col} >= @FromUtc");
            p["FromUtc"] = filter.FromUtc.Value;
        }
        if (filter.ToUtc.HasValue)
        {
            var col = isArchive ? "[FinishedAtUtc]" : "[CreatedAtUtc]";
            sb.Append($" AND {col} <= @ToUtc");
            p["ToUtc"] = filter.ToUtc.Value;
        }

        if (!string.IsNullOrEmpty(filter.Queue))
        {
            sb.Append(" AND [Queue] = @Queue");
            p["Queue"] = filter.Queue;
        }

        if (!isArchive && filter.FailureReason.HasValue)
        {
            // DLQ filtering must stay typed. Matching reason by enum value avoids fragile
            // string searches across stack traces and keeps the query on the FailureReason index.
            sb.Append(" AND [FailureReason] = @FailureReason");
            p["FailureReason"] = (int)filter.FailureReason.Value;
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            if (isArchive)
            {
                sb.Append(" AND ([Id] LIKE @Search OR [Type] LIKE @Search OR [Tags] LIKE @Search)");
            }
            else
            {
                // DLQ search is allowed to inspect ErrorDetails because Top Errors click-through
                // passes the normalized error prefix as SearchTerm. The typed FailureReason filter
                // remains separate above so taxonomy still narrows the candidate set before the
                // operator asks for a free-text family match.
                sb.Append(" AND ([Id] LIKE @Search OR [Type] LIKE @Search OR [Tags] LIKE @Search OR [ErrorDetails] LIKE @Search)");
            }

            p["Search"] = $"%{filter.SearchTerm}%";
        }

        return (sb.ToString(), p);
    }

    private Dictionary<string, object?> MergeParams(Dictionary<string, object?> first, object second)
    {
        var result = new Dictionary<string, object?>(first);
        foreach (var prop in second.GetType().GetProperties())
        {
            result[prop.Name] = prop.GetValue(second);
        }
        return result;
    }

    private static double CalculateJobsPerSecond(long processed, int windowSeconds) =>
        windowSeconds <= 0 ? 0 : processed / (double)windowSeconds;

    private static double CalculateFailureRatePercent(long failed, long processed) =>
        processed == 0 ? 0 : (failed * 100d) / processed;

    private sealed class QueueHealthRow
    {
        public string Queue { get; set; } = "";
        public int Pending { get; set; }
        public double AverageLagSeconds { get; set; }
        public double MaxLagSeconds { get; set; }
    }

    private sealed class ThroughputHealthRow
    {
        public long ProcessedLastMinute { get; set; }
        public long FailedLastMinute { get; set; }
        public long ProcessedLastFiveMinutes { get; set; }
        public long FailedLastFiveMinutes { get; set; }
    }

    private sealed class DlqErrorGroupRow
    {
        public FailureReason FailureReason { get; set; }
        public string ErrorPrefix { get; set; } = "";
        public long Count { get; set; }
        public DateTime LatestFailedAtUtc { get; set; }
    }
}
