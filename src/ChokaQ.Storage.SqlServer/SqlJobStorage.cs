using System.Data;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChokaQ.Storage.SqlServer;

public class SqlJobStorage : IJobStorage
{
    private readonly SqlJobStorageOptions _options;
    private readonly ILogger<SqlJobStorage> _logger;
    private readonly string _schema;

    public SqlJobStorage(
        IOptions<SqlJobStorageOptions> options,
        ILogger<SqlJobStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
        _schema = _options.SchemaName;
    }

    private SqlConnection CreateConnection() => new(_options.ConnectionString);

    // ========================================================================
    // 1. CORE
    // ========================================================================

    public async ValueTask<string> EnqueueAsync(string queue, string jobType, string payload, int priority = 10, string? createdBy = null, string? tags = null, TimeSpan? delay = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var scheduledAt = delay.HasValue ? now.Add(delay.Value) : (DateTime?)null;

        var sql = $@"
            INSERT INTO [{_schema}].[Jobs] 
            ([Id], [Queue], [Type], [Payload], [Tags], [Priority], [Status], [AttemptCount], [CreatedAtUtc], [LastUpdatedUtc], [ScheduledAtUtc], [CreatedBy])
            VALUES 
            (@Id, @Queue, @Type, @Payload, @Tags, @Priority, @Status, 0, @Now, @Now, @ScheduledAt, @CreatedBy);";

        await db.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = id,
            Queue = queue,
            Type = jobType,
            Payload = payload,
            Tags = tags,
            Priority = priority,
            Status = (int)JobStatus.Pending,
            Now = now,
            ScheduledAt = scheduledAt,
            CreatedBy = createdBy
        }, cancellationToken: ct));

        return id;
    }

    public async ValueTask<IEnumerable<JobEntity>> FetchNextBatchAsync(string workerId, int limit, string[]? allowedQueues, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var now = DateTime.UtcNow;

        var queueFilter = allowedQueues != null && allowedQueues.Any()
            ? "AND [Queue] IN @AllowedQueues"
            : "";

        var sql = $@"
            WITH CTE AS (
                SELECT TOP (@Limit) *
                FROM [{_schema}].[Jobs] WITH (UPDLOCK, READPAST)
                WHERE [Status] = 0 -- Pending
                  AND ([ScheduledAtUtc] IS NULL OR [ScheduledAtUtc] <= @Now)
                  {queueFilter}
                ORDER BY [Priority] DESC, [CreatedAtUtc] ASC
            )
            UPDATE CTE
            SET 
                [Status] = 1, -- Fetched
                [WorkerId] = @WorkerId,
                [HeartbeatUtc] = @Now,
                [LastUpdatedUtc] = @Now
            OUTPUT 
                INSERTED.*";

        return await db.QueryAsync<JobEntity>(new CommandDefinition(sql, new
        {
            Limit = limit,
            Now = now,
            WorkerId = workerId,
            AllowedQueues = allowedQueues
        }, cancellationToken: ct));
    }

    public async ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var sql = $"UPDATE [{_schema}].[Jobs] SET [HeartbeatUtc] = SYSUTCDATETIME(), [LastUpdatedUtc] = SYSUTCDATETIME() WHERE [Id] = @Id";
        await db.ExecuteAsync(new CommandDefinition(sql, new { Id = jobId }, cancellationToken: ct));
    }

    // ========================================================================
    // 2. TRANSITIONS (Atomic Move)
    // ========================================================================

    public async Task ArchiveAsSuccessAsync(JobSucceededEntity r, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        await db.OpenAsync(ct);
        using var tran = db.BeginTransaction();

        try
        {
            var delSql = $"DELETE FROM [{_schema}].[Jobs] WHERE [Id] = @Id;";
            var rows = await db.ExecuteAsync(delSql, new { r.Id }, tran);

            if (rows == 0) { tran.Rollback(); return; }

            var insSql = $@"
                INSERT INTO [{_schema}].[JobsSucceeded] 
                ([Id], [Queue], [Type], [Payload], [Tags], [WorkerId], [CreatedBy], [LastModifiedBy], [FinishedAtUtc], [DurationMs])
                VALUES 
                (@Id, @Queue, @Type, @Payload, @Tags, @WorkerId, @CreatedBy, @LastModifiedBy, @FinishedAtUtc, @DurationMs);";

            await db.ExecuteAsync(insSql, r, tran);

            var statsSql = $@"
                UPDATE [{_schema}].[StatsSummary] 
                SET [SucceededTotal] = [SucceededTotal] + 1, [LastActivityUtc] = SYSUTCDATETIME()
                WHERE [Queue] = @Queue;
                
                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO [{_schema}].[StatsSummary] ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                    VALUES (@Queue, 1, 0, 0, SYSUTCDATETIME());
                END";

            await db.ExecuteAsync(statsSql, new { r.Queue }, tran);
            tran.Commit();
        }
        catch { tran.Rollback(); throw; }
    }

    public async Task ArchiveAsMorgueAsync(JobMorgueEntity r, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        await db.OpenAsync(ct);
        using var tran = db.BeginTransaction();

        try
        {
            var delSql = $"DELETE FROM [{_schema}].[Jobs] WHERE [Id] = @Id;";
            var rows = await db.ExecuteAsync(delSql, new { r.Id }, tran);

            if (rows == 0) { tran.Rollback(); return; }

            var insSql = $@"
                INSERT INTO [{_schema}].[JobsMorgue] 
                ([Id], [Queue], [Type], [Payload], [Tags], [ErrorDetails], [AttemptCount], [WorkerId], [CreatedBy], [LastModifiedBy], [FailedAtUtc])
                VALUES 
                (@Id, @Queue, @Type, @Payload, @Tags, @ErrorDetails, @AttemptCount, @WorkerId, @CreatedBy, @LastModifiedBy, @FailedAtUtc);";

            await db.ExecuteAsync(insSql, r, tran);

            var statsSql = $@"
                UPDATE [{_schema}].[StatsSummary] 
                SET [FailedTotal] = [FailedTotal] + 1, [LastActivityUtc] = SYSUTCDATETIME()
                WHERE [Queue] = @Queue;
                
                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO [{_schema}].[StatsSummary] ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                    VALUES (@Queue, 0, 1, 0, SYSUTCDATETIME());
                END";

            await db.ExecuteAsync(statsSql, new { r.Queue }, tran);
            tran.Commit();
        }
        catch { tran.Rollback(); throw; }
    }

    public async Task RetryJobAsync(string jobId, int nextAttempt, TimeSpan delay, string? lastError, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var scheduledAt = DateTime.UtcNow.Add(delay);
        var sql = $@"
            UPDATE [{_schema}].[Jobs] 
            SET [Status] = 0, [AttemptCount] = @Attempt, [ScheduledAtUtc] = @ScheduledAt, 
                [WorkerId] = NULL, [HeartbeatUtc] = NULL, [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id;";

        await db.ExecuteAsync(new CommandDefinition(sql, new { Id = jobId, Attempt = nextAttempt, ScheduledAt = scheduledAt }, cancellationToken: ct));
    }

    public async Task ResurrectJobAsync(string jobId, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        await db.OpenAsync(ct);
        using var tran = db.BeginTransaction();
        try
        {
            var sqlMove = $@"
                INSERT INTO [{_schema}].[Jobs]
                ([Id], [Queue], [Type], [Payload], [Tags], [Priority], [Status], [AttemptCount], [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy], [LastModifiedBy])
                SELECT [Id], [Queue], [Type], [Payload], [Tags], 10, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME(), [CreatedBy], 'Resurrected'
                FROM [{_schema}].[JobsMorgue] WHERE [Id] = @Id;
                DELETE FROM [{_schema}].[JobsMorgue] WHERE [Id] = @Id;";

            var rows = await db.ExecuteAsync(sqlMove, new { Id = jobId }, tran);
            if (rows > 0) tran.Commit(); else tran.Rollback();
        }
        catch { tran.Rollback(); throw; }
    }

    public async Task UpdateJobDataAsync(JobDataUpdateDto update, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var updates = new List<string>();
        if (update.NewPayload != null) updates.Add("[Payload] = @NewPayload");
        if (update.NewTags != null) updates.Add("[Tags] = @NewTags");
        if (update.NewPriority.HasValue) updates.Add("[Priority] = @NewPriority");
        updates.Add("[LastModifiedBy] = @UpdatedBy");
        updates.Add("[LastUpdatedUtc] = SYSUTCDATETIME()");

        var sql = $@"UPDATE [{_schema}].[Jobs] SET {string.Join(", ", updates)} WHERE [Id] = @JobId AND [Status] = 0;";
        await db.ExecuteAsync(new CommandDefinition(sql, update, cancellationToken: ct));
    }

    public async Task PurgeJobAsync(string id, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var sql = $@"
            DELETE FROM [{_schema}].[Jobs] WHERE [Id] = @Id;
            DELETE FROM [{_schema}].[JobsSucceeded] WHERE [Id] = @Id;
            DELETE FROM [{_schema}].[JobsMorgue] WHERE [Id] = @Id;";
        await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async ValueTask<StatsSummaryEntity?> GetSummaryStatsAsync(string queue, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var sql = $"SELECT * FROM [{_schema}].[StatsSummary] WHERE [Queue] = @Queue";
        return await db.QuerySingleOrDefaultAsync<StatsSummaryEntity>(new CommandDefinition(sql, new { Queue = queue }, cancellationToken: ct));
    }

    public async ValueTask<IEnumerable<JobSucceededEntity>> GetHistoryJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var sql = $"SELECT * FROM [{_schema}].[JobsSucceeded] WHERE [Queue] = @Queue ORDER BY [FinishedAtUtc] DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
        return await db.QueryAsync<JobSucceededEntity>(new CommandDefinition(sql, new { Queue = queue, Skip = skip, Take = take }, cancellationToken: ct));
    }

    public async ValueTask<IEnumerable<JobMorgueEntity>> GetMorgueJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var sql = $"SELECT * FROM [{_schema}].[JobsMorgue] WHERE [Queue] = @Queue ORDER BY [FailedAtUtc] DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
        return await db.QueryAsync<JobMorgueEntity>(new CommandDefinition(sql, new { Queue = queue, Skip = skip, Take = take }, cancellationToken: ct));
    }

    public async ValueTask<JobEntity?> GetJobEntityAsync(string id, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        return await db.QuerySingleOrDefaultAsync<JobEntity>($"SELECT * FROM [{_schema}].[Jobs] WHERE [Id] = @Id", new { Id = id });
    }

    public async ValueTask<JobSucceededEntity?> GetSucceededEntityAsync(string id, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        return await db.QuerySingleOrDefaultAsync<JobSucceededEntity>($"SELECT * FROM [{_schema}].[JobsSucceeded] WHERE [Id] = @Id", new { Id = id });
    }

    public async ValueTask<JobMorgueEntity?> GetMorgueEntityAsync(string id, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        return await db.QuerySingleOrDefaultAsync<JobMorgueEntity>($"SELECT * FROM [{_schema}].[JobsMorgue] WHERE [Id] = @Id", new { Id = id });
    }

    public async ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default)
    {
        using var db = CreateConnection();
        return await db.QueryAsync<QueueEntity>($"SELECT * FROM [{_schema}].[Queues]");
    }

    public async ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var sql = $@"
            UPDATE [{_schema}].[Queues] SET [IsPaused] = @IsPaused, [LastUpdatedUtc] = SYSUTCDATETIME() WHERE [Name] = @Name;
            IF @@ROWCOUNT = 0 INSERT INTO [{_schema}].[Queues] ([Name], [IsPaused], [LastUpdatedUtc]) VALUES (@Name, @IsPaused, SYSUTCDATETIME());";
        await db.ExecuteAsync(new CommandDefinition(sql, new { Name = queueName, IsPaused = isPaused }, cancellationToken: ct));
    }

    public async ValueTask<int> MarkZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default)
    {
        using var db = CreateConnection();
        var threshold = DateTime.UtcNow.AddSeconds(-globalTimeoutSeconds);
        var sql = $"UPDATE [{_schema}].[Jobs] SET [Status] = 0, [WorkerId] = NULL, [HeartbeatUtc] = NULL, [LastUpdatedUtc] = SYSUTCDATETIME() WHERE [Status] = 1 AND [HeartbeatUtc] < @Threshold";
        return await db.ExecuteAsync(new CommandDefinition(sql, new { Threshold = threshold }, cancellationToken: ct));
    }

    public ValueTask UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default) => ValueTask.CompletedTask;
}