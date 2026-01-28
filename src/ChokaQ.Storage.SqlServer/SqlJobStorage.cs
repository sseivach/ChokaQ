using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using Dapper;
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

    public SqlJobStorage(IOptions<SqlJobStorageOptions> options)
    {
        _options = options.Value;
        _schema = _options.SchemaName;
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
                $"SELECT [Id] FROM [{_schema}].[JobsHot] WHERE [IdempotencyKey] = @Key",
                new { Key = idempotencyKey });

            if (existingId != null)
                return existingId;
        }

        var sql = $@"
            INSERT INTO [{_schema}].[JobsHot] 
            ([Id], [Queue], [Type], [Payload], [Tags], [IdempotencyKey], [Priority], [Status], 
             [AttemptCount], [ScheduledAtUtc], [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy])
            VALUES 
            (@Id, @Queue, @Type, @Payload, @Tags, @IdempotencyKey, @Priority, 0,
             0, @ScheduledAt, SYSUTCDATETIME(), SYSUTCDATETIME(), @CreatedBy);
            
            -- Ensure queue exists in config
            IF NOT EXISTS (SELECT 1 FROM [{_schema}].[Queues] WHERE [Name] = @Queue)
                INSERT INTO [{_schema}].[Queues] ([Name], [IsPaused], [IsActive], [LastUpdatedUtc])
                VALUES (@Queue, 0, 1, SYSUTCDATETIME());
            
            -- Ensure stats row exists (MERGE for atomicity)
            MERGE [{_schema}].[StatsSummary] AS target
            USING (SELECT @Queue AS Queue) AS source
            ON target.[Queue] = source.Queue
            WHEN NOT MATCHED THEN
                INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal])
                VALUES (@Queue, 0, 0, 0);";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
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
        }, cancellationToken: ct));

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

        var sql = $@"
            WITH CTE AS (
                SELECT TOP (@Limit) h.*
                FROM [{_schema}].[JobsHot] h WITH (UPDLOCK, READPAST)
                LEFT JOIN [{_schema}].[Queues] q ON q.[Name] = h.[Queue]
                WHERE h.[Status] = 0
                  AND (h.[ScheduledAtUtc] IS NULL OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())
                  AND (q.[IsPaused] = 0 OR q.[IsPaused] IS NULL)
                  {queueFilter}
                ORDER BY h.[Priority] DESC, ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
            )
            UPDATE CTE 
            SET [Status] = 1,
                [WorkerId] = @WorkerId,
                [AttemptCount] = [AttemptCount] + 1,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            OUTPUT inserted.*;";

        return await conn.QueryAsync<JobHotEntity>(new CommandDefinition(
            sql,
            new { Limit = batchSize, WorkerId = workerId, Queues = allowedQueues },
            cancellationToken: ct));
    }

    public async ValueTask MarkAsProcessingAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var sql = $@"
            UPDATE [{_schema}].[JobsHot]
            SET [Status] = 2,
                [StartedAtUtc] = SYSUTCDATETIME(),
                [HeartbeatUtc] = SYSUTCDATETIME(),
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = jobId }, cancellationToken: ct));
    }

    public async ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var sql = $@"
            UPDATE [{_schema}].[JobsHot]
            SET [HeartbeatUtc] = SYSUTCDATETIME(),
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = jobId }, cancellationToken: ct));
    }

    public async ValueTask<JobHotEntity?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var sql = $"SELECT * FROM [{_schema}].[JobsHot] WHERE [Id] = @Id";
        return await conn.QueryFirstOrDefaultAsync<JobHotEntity>(
            new CommandDefinition(sql, new { Id = jobId }, cancellationToken: ct));
    }

    // ========================================================================
    // ATOMIC TRANSITIONS (Three Pillars)
    // ========================================================================

    public async ValueTask ArchiveSucceededAsync(string jobId, double? durationMs = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // INSERT-first with OUTPUT for atomicity (no data loss on crash)
        var sql = $@"
            DECLARE @Queue varchar(255);
            SELECT @Queue = [Queue] FROM [{_schema}].[JobsHot] WHERE [Id] = @Id;

            -- 1. Archive to JobsArchive using OUTPUT from DELETE
            INSERT INTO [{_schema}].[JobsArchive]
            ([Id], [Queue], [Type], [Payload], [Tags], [AttemptCount], [WorkerId], 
             [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [StartedAtUtc], [FinishedAtUtc], [DurationMs])
            SELECT [Id], [Queue], [Type], [Payload], [Tags], [AttemptCount], [WorkerId],
                   [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [StartedAtUtc], SYSUTCDATETIME(), @DurationMs
            FROM [{_schema}].[JobsHot]
            WHERE [Id] = @Id;

            -- 2. Delete from Hot
            DELETE FROM [{_schema}].[JobsHot] WHERE [Id] = @Id;

            -- 3. Update stats (MERGE for upsert)
            MERGE [{_schema}].[StatsSummary] AS target
            USING (SELECT @Queue AS Queue) AS source
            ON target.[Queue] = source.Queue
            WHEN MATCHED THEN
                UPDATE SET [SucceededTotal] = [SucceededTotal] + 1,
                           [LastActivityUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                VALUES (@Queue, 1, 0, 0, SYSUTCDATETIME());";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = jobId, DurationMs = durationMs }, cancellationToken: ct));
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

        var sql = $@"
            DECLARE @Queue varchar(255);
            SELECT @Queue = [Queue] FROM [{_schema}].[JobsHot] WHERE [Id] = @Id;

            -- 1. Insert into DLQ
            INSERT INTO [{_schema}].[JobsDLQ]
            ([Id], [Queue], [Type], [Payload], [Tags], [FailureReason], [ErrorDetails], [AttemptCount],
             [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [FailedAtUtc])
            SELECT [Id], [Queue], [Type], [Payload], [Tags], @Reason, @Error, [AttemptCount],
                   [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], SYSUTCDATETIME()
            FROM [{_schema}].[JobsHot]
            WHERE [Id] = @Id;

            -- 2. Delete from Hot
            DELETE FROM [{_schema}].[JobsHot] WHERE [Id] = @Id;

            -- 3. Update stats (MERGE for upsert)
            MERGE [{_schema}].[StatsSummary] AS target
            USING (SELECT @Queue AS Queue) AS source
            ON target.[Queue] = source.Queue
            WHEN MATCHED THEN
                UPDATE SET [FailedTotal] = [FailedTotal] + 1,
                           [LastActivityUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                VALUES (@Queue, 0, 1, 0, SYSUTCDATETIME());";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = jobId,
            Reason = (int)reason,
            Error = errorDetails
        }, cancellationToken: ct));
    }

    public async ValueTask ResurrectAsync(
        string jobId,
        JobDataUpdateDto? updates = null,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var sql = $@"
            DECLARE @Queue varchar(255);
            SELECT @Queue = [Queue] FROM [{_schema}].[JobsDLQ] WHERE [Id] = @Id;

            -- 1. Move to Hot (reset state)
            INSERT INTO [{_schema}].[JobsHot]
            ([Id], [Queue], [Type], [Payload], [Tags], [Priority], [Status], [AttemptCount],
             [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy], [LastModifiedBy])
            SELECT [Id], [Queue], [Type], 
                   ISNULL(@NewPayload, [Payload]), 
                   ISNULL(@NewTags, [Tags]), 
                   ISNULL(@NewPriority, 10), 
                   0, 0,
                   [CreatedAtUtc], SYSUTCDATETIME(), [CreatedBy], @ResurrectedBy
            FROM [{_schema}].[JobsDLQ]
            WHERE [Id] = @Id;

            -- 2. Remove from DLQ
            DELETE FROM [{_schema}].[JobsDLQ] WHERE [Id] = @Id;

            -- 3. Decrement failed stats
            UPDATE [{_schema}].[StatsSummary]
            SET [FailedTotal] = CASE WHEN [FailedTotal] > 0 THEN [FailedTotal] - 1 ELSE 0 END,
                [LastActivityUtc] = SYSUTCDATETIME()
            WHERE [Queue] = @Queue;";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = jobId,
            NewPayload = updates?.Payload,
            NewTags = updates?.Tags,
            NewPriority = updates?.Priority,
            ResurrectedBy = resurrectedBy
        }, cancellationToken: ct));
    }

    public async ValueTask<int> ResurrectBatchAsync(string[] jobIds, string? resurrectedBy = null, CancellationToken ct = default)
    {
        if (jobIds.Length == 0) return 0;

        await using var conn = await OpenConnectionAsync(ct);
        int total = 0;

        // Process in batches of 1000
        foreach (var batch in jobIds.Chunk(1000))
        {
            var sql = $@"
                -- Move batch to Hot
                INSERT INTO [{_schema}].[JobsHot]
                ([Id], [Queue], [Type], [Payload], [Tags], [Priority], [Status], [AttemptCount],
                 [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy], [LastModifiedBy])
                SELECT [Id], [Queue], [Type], [Payload], [Tags], 10, 0, 0,
                       [CreatedAtUtc], SYSUTCDATETIME(), [CreatedBy], @ResurrectedBy
                FROM [{_schema}].[JobsDLQ]
                WHERE [Id] IN @Ids;

                -- Remove from DLQ
                DELETE FROM [{_schema}].[JobsDLQ] WHERE [Id] IN @Ids;";

            var affected = await conn.ExecuteAsync(new CommandDefinition(
                sql, new { Ids = batch, ResurrectedBy = resurrectedBy }, cancellationToken: ct));
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

        var sql = $@"
            DECLARE @Queue varchar(255);
            SELECT @Queue = [Queue] FROM [{_schema}].[JobsHot] WHERE [Id] = @Id;

            UPDATE [{_schema}].[JobsHot]
            SET [Status] = 0,
                [AttemptCount] = @Attempt,
                [ScheduledAtUtc] = @ScheduledAt,
                [WorkerId] = NULL,
                [HeartbeatUtc] = NULL,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id;

            -- Increment retry counter (MERGE for upsert)
            MERGE [{_schema}].[StatsSummary] AS target
            USING (SELECT @Queue AS Queue) AS source
            ON target.[Queue] = source.Queue
            WHEN MATCHED THEN
                UPDATE SET [RetriedTotal] = [RetriedTotal] + 1,
                           [LastActivityUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                VALUES (@Queue, 0, 0, 1, SYSUTCDATETIME());";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = jobId,
            Attempt = newAttemptCount,
            ScheduledAt = scheduledAtUtc
        }, cancellationToken: ct));
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

        // Safety Gate: Only update Pending jobs
        var sql = $@"
            UPDATE [{_schema}].[JobsHot]
            SET [Payload] = ISNULL(@Payload, [Payload]),
                [Tags] = ISNULL(@Tags, [Tags]),
                [Priority] = ISNULL(@Priority, [Priority]),
                [LastModifiedBy] = @ModifiedBy,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id AND [Status] = 0";

        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = jobId,
            updates.Payload,
            updates.Tags,
            updates.Priority,
            ModifiedBy = modifiedBy
        }, cancellationToken: ct));

        return affected > 0;
    }

    public async ValueTask PurgeDLQAsync(string[] jobIds, CancellationToken ct = default)
    {
        if (jobIds.Length == 0) return;

        await using var conn = await OpenConnectionAsync(ct);
        var sql = $"DELETE FROM [{_schema}].[JobsDLQ] WHERE [Id] IN @Ids";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Ids = jobIds }, cancellationToken: ct));
    }

    public async ValueTask<int> PurgeArchiveAsync(DateTime olderThan, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var sql = $"DELETE FROM [{_schema}].[JobsArchive] WHERE [FinishedAtUtc] < @CutOff";
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { CutOff = olderThan }, cancellationToken: ct));
    }

    // ========================================================================
    // OBSERVABILITY (Dashboard)
    // ========================================================================

    public async ValueTask<StatsSummaryEntity> GetSummaryStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // Hybrid: Real-time counts from Hot + pre-aggregated totals from StatsSummary
        var sql = $@"
            SELECT 
                CAST(NULL AS NVARCHAR(100)) AS [Queue],
                (SELECT COUNT(1) FROM [{_schema}].[JobsHot] WHERE [Status] = 0) AS [Pending],
                (SELECT COUNT(1) FROM [{_schema}].[JobsHot] WHERE [Status] = 1) AS [Fetched],
                (SELECT COUNT(1) FROM [{_schema}].[JobsHot] WHERE [Status] = 2) AS [Processing],
                (SELECT ISNULL(SUM([SucceededTotal]), 0) FROM [{_schema}].[StatsSummary]) AS [SucceededTotal],
                (SELECT ISNULL(SUM([FailedTotal]), 0) FROM [{_schema}].[StatsSummary]) AS [FailedTotal],
                (SELECT ISNULL(SUM([RetriedTotal]), 0) FROM [{_schema}].[StatsSummary]) AS [RetriedTotal],
                CAST(
                    (SELECT COUNT(1) FROM [{_schema}].[JobsHot]) + 
                    (SELECT COUNT(1) FROM [{_schema}].[JobsArchive]) + 
                    (SELECT COUNT(1) FROM [{_schema}].[JobsDLQ]) 
                AS BIGINT) AS [Total],
                (SELECT MAX([LastActivityUtc]) FROM [{_schema}].[StatsSummary]) AS [LastActivityUtc]";

        return await conn.QuerySingleAsync<StatsSummaryEntity>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async ValueTask<IEnumerable<StatsSummaryEntity>> GetQueueStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // Per-queue hybrid stats
        var sql = $@"
            SELECT 
                q.[Name] AS [Queue],
                (SELECT COUNT(1) FROM [{_schema}].[JobsHot] WHERE [Queue] = q.[Name] AND [Status] = 0) AS [Pending],
                (SELECT COUNT(1) FROM [{_schema}].[JobsHot] WHERE [Queue] = q.[Name] AND [Status] = 1) AS [Fetched],
                (SELECT COUNT(1) FROM [{_schema}].[JobsHot] WHERE [Queue] = q.[Name] AND [Status] = 2) AS [Processing],
                ISNULL(s.[SucceededTotal], 0) AS [SucceededTotal],
                ISNULL(s.[FailedTotal], 0) AS [FailedTotal],
                ISNULL(s.[RetriedTotal], 0) AS [RetriedTotal],
                CAST(
                    (SELECT COUNT(1) FROM [{_schema}].[JobsHot] WHERE [Queue] = q.[Name]) + 
                    (SELECT COUNT(1) FROM [{_schema}].[JobsArchive] WHERE [Queue] = q.[Name]) + 
                    (SELECT COUNT(1) FROM [{_schema}].[JobsDLQ] WHERE [Queue] = q.[Name])
                AS BIGINT) AS [Total],
                s.[LastActivityUtc]
            FROM [{_schema}].[Queues] q
            LEFT JOIN [{_schema}].[StatsSummary] s ON s.[Queue] = q.[Name]
            ORDER BY q.[Name]";

        return await conn.QueryAsync<StatsSummaryEntity>(new CommandDefinition(sql, cancellationToken: ct));
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

        var sql = $@"
            SELECT TOP (@Limit) * 
            FROM [{_schema}].[JobsHot] 
            {where}
            ORDER BY [CreatedAtUtc] DESC";

        return await conn.QueryAsync<JobHotEntity>(new CommandDefinition(sql, new
        {
            Limit = limit,
            Status = statusFilter.HasValue ? (int)statusFilter.Value : (int?)null,
            Queue = queueFilter,
            Search = $"%{searchTerm}%"
        }, cancellationToken: ct));
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

        var sql = $@"
            SELECT TOP (@Limit) * 
            FROM [{_schema}].[JobsArchive] 
            {where}
            ORDER BY [FinishedAtUtc] DESC";

        return await conn.QueryAsync<JobArchiveEntity>(new CommandDefinition(sql, new
        {
            Limit = limit,
            Queue = queueFilter,
            FromDate = fromDate,
            ToDate = toDate,
            TagFilter = $"%{tagFilter}%"
        }, cancellationToken: ct));
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

        var sql = $@"
            SELECT TOP (@Limit) * 
            FROM [{_schema}].[JobsDLQ] 
            {where}
            ORDER BY [FailedAtUtc] DESC";

        return await conn.QueryAsync<JobDLQEntity>(new CommandDefinition(sql, new
        {
            Limit = limit,
            Queue = queueFilter,
            Reason = reasonFilter.HasValue ? (int)reasonFilter.Value : (int?)null,
            Search = $"%{searchTerm}%"
        }, cancellationToken: ct));
    }

    public async ValueTask<JobArchiveEntity?> GetArchiveJobAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var sql = $"SELECT * FROM [{_schema}].[JobsArchive] WHERE [Id] = @Id";
        return await conn.QueryFirstOrDefaultAsync<JobArchiveEntity>(
            new CommandDefinition(sql, new { Id = jobId }, cancellationToken: ct));
    }

    public async ValueTask<JobDLQEntity?> GetDLQJobAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var sql = $"SELECT * FROM [{_schema}].[JobsDLQ] WHERE [Id] = @Id";
        return await conn.QueryFirstOrDefaultAsync<JobDLQEntity>(
            new CommandDefinition(sql, new { Id = jobId }, cancellationToken: ct));
    }

    // ========================================================================
    // QUEUE MANAGEMENT
    // ========================================================================

    public async ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var sql = $@"
            SELECT q.[Name], q.[IsPaused], q.[IsActive], q.[ZombieTimeoutSeconds], q.[LastUpdatedUtc]
            FROM [{_schema}].[Queues] q
            ORDER BY q.[Name]";

        return await conn.QueryAsync<QueueEntity>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async ValueTask SetQueuePausedAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // MERGE for upsert
        var sql = $@"
            MERGE [{_schema}].[Queues] AS t
            USING (SELECT @Name AS Name) AS s ON (t.[Name] = s.Name)
            WHEN MATCHED THEN 
                UPDATE SET [IsPaused] = @IsPaused, [LastUpdatedUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN 
                INSERT ([Name], [IsPaused], [IsActive], [LastUpdatedUtc]) 
                VALUES (@Name, @IsPaused, 1, SYSUTCDATETIME());";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { Name = queueName, IsPaused = isPaused }, cancellationToken: ct));
    }

    public async ValueTask SetQueueZombieTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var sql = $@"
            MERGE [{_schema}].[Queues] AS t
            USING (SELECT @Name AS Name) AS s ON (t.[Name] = s.Name)
            WHEN MATCHED THEN 
                UPDATE SET [ZombieTimeoutSeconds] = @Timeout, [LastUpdatedUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN 
                INSERT ([Name], [IsPaused], [IsActive], [ZombieTimeoutSeconds], [LastUpdatedUtc]) 
                VALUES (@Name, 0, 1, @Timeout, SYSUTCDATETIME());";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { Name = queueName, Timeout = timeoutSeconds }, cancellationToken: ct));
    }

    // ========================================================================
    // ZOMBIE DETECTION
    // ========================================================================

    public async ValueTask<int> ArchiveZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // Find and archive zombies - jobs stuck in Processing/Fetched with expired heartbeat
        var sql = $@"
            DECLARE @ZombieIds TABLE (Id varchar(50), Queue varchar(255));

            -- Find zombies (Processing or Fetched with expired heartbeat)
            INSERT INTO @ZombieIds (Id, Queue)
            SELECT h.[Id], h.[Queue]
            FROM [{_schema}].[JobsHot] h
            LEFT JOIN [{_schema}].[Queues] q ON q.[Name] = h.[Queue]
            WHERE h.[Status] IN (1, 2)
              AND DATEDIFF(SECOND, ISNULL(h.[HeartbeatUtc], h.[LastUpdatedUtc]), SYSUTCDATETIME()) 
                  > ISNULL(q.[ZombieTimeoutSeconds], @GlobalTimeout);

            -- Archive to DLQ
            INSERT INTO [{_schema}].[JobsDLQ]
            ([Id], [Queue], [Type], [Payload], [Tags], [FailureReason], [ErrorDetails], [AttemptCount],
             [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [FailedAtUtc])
            SELECT h.[Id], h.[Queue], h.[Type], h.[Payload], h.[Tags], 
                   2, -- Zombie
                   'Zombie: Worker heartbeat expired',
                   h.[AttemptCount], h.[WorkerId], h.[CreatedBy], h.[LastModifiedBy], 
                   h.[CreatedAtUtc], SYSUTCDATETIME()
            FROM [{_schema}].[JobsHot] h
            INNER JOIN @ZombieIds z ON z.Id = h.Id;

            -- Delete from Hot
            DELETE FROM [{_schema}].[JobsHot] 
            WHERE [Id] IN (SELECT Id FROM @ZombieIds);

            -- Update stats per queue
            UPDATE s
            SET s.[FailedTotal] = s.[FailedTotal] + counts.ZombieCount,
                s.[LastActivityUtc] = SYSUTCDATETIME()
            FROM [{_schema}].[StatsSummary] s
            INNER JOIN (
                SELECT Queue, COUNT(*) as ZombieCount FROM @ZombieIds GROUP BY Queue
            ) counts ON counts.Queue = s.[Queue];

            SELECT COUNT(*) FROM @ZombieIds;";

        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { GlobalTimeout = globalTimeoutSeconds }, cancellationToken: ct));
    }
}
