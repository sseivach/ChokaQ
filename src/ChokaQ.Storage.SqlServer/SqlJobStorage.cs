using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;

namespace ChokaQ.Storage.SqlServer;

public class SqlJobStorage : IJobStorage
{
    private readonly SqlJobStorageOptions _options;
    private readonly string _connectionString;

    public SqlJobStorage(IOptions<SqlJobStorageOptions> options)
    {
        _options = options.Value;
        _connectionString = _options.ConnectionString;
    }

    private async Task<IDbConnection> GetConnectionAsync()
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    // ========================================================================
    // 1. CORE (Worker & Producer)
    // ========================================================================

    public async ValueTask<string> EnqueueAsync(
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
        using var conn = await GetConnectionAsync();
        var id = Guid.NewGuid().ToString("N");
        var runAt = DateTime.UtcNow.Add(delay ?? TimeSpan.Zero);

        const string sql = @"
            INSERT INTO [{0}].[Jobs] 
            ([Id], [Queue], [Type], [Payload], [Status], [Priority], [CreatedAtUtc], [RunAtUtc], [CreatedBy], [Tags], [IdempotencyKey], [AttemptCount])
            VALUES 
            (@Id, @Queue, @Type, @Payload, 0, @Priority, SYSUTCDATETIME(), @RunAt, @CreatedBy, @Tags, @IdempotencyKey, 0)";

        await conn.ExecuteAsync(string.Format(sql, _options.Schema), new
        {
            Id = id,
            Queue = queue,
            Type = jobType,
            Payload = payload,
            Priority = priority,
            RunAt = runAt,
            CreatedBy = createdBy,
            Tags = tags,
            IdempotencyKey = idempotencyKey
        });

        return id;
    }

    public async ValueTask<IEnumerable<JobEntity>> FetchNextBatchAsync(
        string workerId,
        int limit,
        string[]? allowedQueues,
        CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();

        // Магия T-SQL: UPDATE с OUTPUT для атомарного захвата задач
        var sql = $@"
            WITH CTE AS (
                SELECT TOP (@Limit) *
                FROM [{_options.Schema}].[Jobs] WITH (UPDLOCK, READPAST)
                WHERE [Status] = 0 -- Pending
                  AND [RunAtUtc] <= SYSUTCDATETIME()
                  {(allowedQueues != null && allowedQueues.Any() ? "AND [Queue] IN @Queues" : "")}
                ORDER BY [Priority] DESC, [CreatedAtUtc] ASC
            )
            UPDATE CTE 
            SET [Status] = 1, -- Fetched
                [LockedBy] = @WorkerId, 
                [LockedAtUtc] = SYSUTCDATETIME()
            OUTPUT inserted.*;";

        return await conn.QueryAsync<JobEntity>(sql, new { Limit = limit, WorkerId = workerId, Queues = allowedQueues });
    }

    public async ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        const string sql = "UPDATE [{0}].[Jobs] SET [LockedAtUtc] = SYSUTCDATETIME() WHERE [Id] = @Id";
        await conn.ExecuteAsync(string.Format(sql, _options.Schema), new { Id = jobId });
    }

    // ========================================================================
    // 2. TRANSITIONS (Atomic Move)
    // ========================================================================

    public async Task RetryJobAsync(string jobId, int nextAttempt, TimeSpan delay, string? lastError, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        const string sql = @"
            UPDATE [{0}].[Jobs]
            SET [Status] = 0, -- Back to Pending
                [AttemptCount] = @NextAttempt,
                [RunAtUtc] = DATEADD(second, @DelaySec, SYSUTCDATETIME()),
                [LastError] = @Error,
                [LockedBy] = NULL,
                [LockedAtUtc] = NULL
            WHERE [Id] = @Id";

        await conn.ExecuteAsync(string.Format(sql, _options.Schema), new
        {
            Id = jobId,
            NextAttempt = nextAttempt,
            DelaySec = delay.TotalSeconds,
            Error = lastError
        });
    }

    /// <summary>
    /// Transitions job from Fetched (1) to Processing (2).
    /// This is what triggers the 'Running' spinner on the Dashboard.
    /// </summary>
    public async Task MarkAsProcessingAsync(string jobId, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();

        // Status 2 = Processing. 
        // We also update LockedAtUtc to give the job a fresh lease of life 
        // so the Zombie monitor doesn't kill it right at the start.
        const string sql = @"
            UPDATE [{0}].[Jobs]
            SET [Status] = 2, 
                [LockedAtUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id";

        await conn.ExecuteAsync(string.Format(sql, _options.Schema), new { Id = jobId });
    }

    public async Task ArchiveAsSuccessAsync(JobSucceededEntity record, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        using var trans = conn.BeginTransaction();
        try
        {
            const string insertSql = @"
                INSERT INTO [{0}].[JobsSucceeded] ([Id], [Queue], [Type], [Payload], [CreatedAtUtc], [FinishedAtUtc], [DurationMs], [CreatedBy])
                VALUES (@Id, @Queue, @Type, @Payload, @CreatedAtUtc, SYSUTCDATETIME(), @DurationMs, @CreatedBy)";

            const string deleteSql = "DELETE FROM [{0}].[Jobs] WHERE [Id] = @Id";

            await conn.ExecuteAsync(string.Format(insertSql, _options.Schema), record, trans);
            await conn.ExecuteAsync(string.Format(deleteSql, _options.Schema), new { Id = record.Id }, trans);
            trans.Commit();
        }
        catch { trans.Rollback(); throw; }
    }

    public async Task ArchiveAsMorgueAsync(JobMorgueEntity record, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        using var trans = conn.BeginTransaction();
        try
        {
            const string insertSql = @"
                INSERT INTO [{0}].[JobsMorgue] ([Id], [Queue], [Type], [Payload], [CreatedAtUtc], [FailedAtUtc], [ErrorDetails], [AttemptCount], [CreatedBy])
                VALUES (@Id, @Queue, @Type, @Payload, @CreatedAtUtc, SYSUTCDATETIME(), @ErrorDetails, @AttemptCount, @CreatedBy)";

            const string deleteSql = "DELETE FROM [{0}].[Jobs] WHERE [Id] = @Id";

            await conn.ExecuteAsync(string.Format(insertSql, _options.Schema), record, trans);
            await conn.ExecuteAsync(string.Format(deleteSql, _options.Schema), new { Id = record.Id }, trans);
            trans.Commit();
        }
        catch { trans.Rollback(); throw; }
    }

    public async Task ResurrectJobAsync(string jobId, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        using var trans = conn.BeginTransaction();
        try
        {
            // Move from Morgue back to Jobs table
            const string moveSql = @"
                INSERT INTO [{0}].[Jobs] ([Id], [Queue], [Type], [Payload], [Status], [Priority], [CreatedAtUtc], [RunAtUtc], [CreatedBy], [AttemptCount])
                SELECT [Id], [Queue], [Type], [Payload], 0, 10, [CreatedAtUtc], SYSUTCDATETIME(), [CreatedBy], 0
                FROM [{0}].[JobsMorgue] WHERE [Id] = @Id";

            const string deleteSql = "DELETE FROM [{0}].[JobsMorgue] WHERE [Id] = @Id";

            await conn.ExecuteAsync(string.Format(moveSql, _options.Schema), new { Id = jobId }, trans);
            await conn.ExecuteAsync(string.Format(deleteSql, _options.Schema), new { Id = jobId }, trans);
            trans.Commit();
        }
        catch { trans.Rollback(); throw; }
    }

    // ========================================================================
    // 3. DIVINE MODE & OBSERVABILITY
    // ========================================================================

    public async ValueTask<JobCountsDto> GetJobCountsAsync(CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        const string sql = @"
            SELECT 
                (SELECT COUNT(1) FROM [{0}].[Jobs] WHERE [Status] = 0) as Pending,
                (SELECT COUNT(1) FROM [{0}].[Jobs] WHERE [Status] = 2) as Processing,
                (SELECT COUNT(1) FROM [{0}].[JobsSucceeded]) as Succeeded,
                (SELECT COUNT(1) FROM [{0}].[JobsMorgue]) as Failed,
                (SELECT COUNT(1) FROM [{0}].[Jobs] WHERE [AttemptCount] > 0 AND [Status] = 0) as Retrying";

        return await conn.QuerySingleAsync<JobCountsDto>(string.Format(sql, _options.Schema));
    }

    public async ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        const string sql = @"
            SELECT q.*, 
                (SELECT COUNT(1) FROM [{0}].[Jobs] WHERE [Queue] = q.Name AND [Status] = 0) as PendingCount,
                (SELECT COUNT(1) FROM [{0}].[Jobs] WHERE [Queue] = q.Name AND [Status] = 2) as ProcessingCount,
                (SELECT COUNT(1) FROM [{0}].[JobsSucceeded] WHERE [Queue] = q.Name) as SucceededCount,
                (SELECT COUNT(1) FROM [{0}].[JobsMorgue] WHERE [Queue] = q.Name) as FailedCount
            FROM [{0}].[Queues] q";
        return await conn.QueryAsync<QueueEntity>(string.Format(sql, _options.Schema));
    }

    public async Task UpdateJobPriorityAsync(string jobId, int priority, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        const string sql = "UPDATE [{0}].[Jobs] SET [Priority] = @Priority WHERE [Id] = @Id";
        await conn.ExecuteAsync(string.Format(sql, _options.Schema), new { Id = jobId, Priority = priority });
    }

    public async Task UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        const string sql = "UPDATE [{0}].[Queues] SET [ZombieTimeoutSeconds] = @T, [LastUpdatedUtc] = SYSUTCDATETIME() WHERE [Name] = @N";
        await conn.ExecuteAsync(string.Format(sql, _options.Schema), new { N = queueName, T = timeoutSeconds });
    }

    public async ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        const string sql = @"
            MERGE [{0}].[Queues] AS t USING (SELECT @N AS Name) AS s ON (t.Name = s.Name)
            WHEN MATCHED THEN UPDATE SET [IsPaused] = @P, [LastUpdatedUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT ([Name], [IsPaused], [IsActive], [LastUpdatedUtc]) VALUES (@N, @P, 1, SYSUTCDATETIME());";
        await conn.ExecuteAsync(string.Format(sql, _options.Schema), new { N = queueName, P = isPaused });
    }

    public async Task PurgeJobAsync(string id, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        const string sql = @"
            DELETE FROM [{0}].[Jobs] WHERE [Id] = @Id;
            DELETE FROM [{0}].[JobsSucceeded] WHERE [Id] = @Id;
            DELETE FROM [{0}].[JobsMorgue] WHERE [Id] = @Id;";
        await conn.ExecuteAsync(string.Format(sql, _options.Schema), new { Id = id });
    }

    public async ValueTask<int> MarkZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        // Reset jobs that are stuck in 'Processing' or 'Fetched' state too long
        const string sql = @"
            UPDATE [{0}].[Jobs]
            SET [Status] = 0, [LockedBy] = NULL, [LockedAtUtc] = NULL, [AttemptCount] = [AttemptCount] + 1
            WHERE [Status] IN (1, 2) AND DATEDIFF(second, [LockedAtUtc], SYSUTCDATETIME()) > @Timeout";
        return await conn.ExecuteAsync(string.Format(sql, _options.Schema), new { Timeout = globalTimeoutSeconds });
    }

    // --- Lookups (Simplified) ---
    public async ValueTask<JobEntity?> GetJobEntityAsync(string id, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryFirstOrDefaultAsync<JobEntity>(string.Format("SELECT * FROM [{0}].[Jobs] WHERE Id = @Id", _options.Schema), new { Id = id });
    }

    public async ValueTask<IEnumerable<JobEntity>> GetActiveJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryAsync<JobEntity>(string.Format("SELECT * FROM [{0}].[Jobs] WHERE Queue = @Q ORDER BY CreatedAtUtc DESC OFFSET @S ROWS FETCH NEXT @T ROWS ONLY", _options.Schema), new { Q = queue, S = skip, T = take });
    }

    public async ValueTask<IEnumerable<JobSucceededEntity>> GetHistoryJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryAsync<JobSucceededEntity>(string.Format("SELECT * FROM [{0}].[JobsSucceeded] WHERE Queue = @Q ORDER BY FinishedAtUtc DESC OFFSET @S ROWS FETCH NEXT @T ROWS ONLY", _options.Schema), new { Q = queue, S = skip, T = take });
    }

    public async ValueTask<IEnumerable<JobMorgueEntity>> GetMorgueJobsAsync(string queue, int skip, int take, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryAsync<JobMorgueEntity>(string.Format("SELECT * FROM [{0}].[JobsMorgue] WHERE Queue = @Q ORDER BY FailedAtUtc DESC OFFSET @S ROWS FETCH NEXT @T ROWS ONLY", _options.Schema), new { Q = queue, S = skip, T = take });
    }

    public async Task UpdateJobDataAsync(JobDataUpdateDto update, CancellationToken ct = default)
    {
        using var conn = await GetConnectionAsync();
        const string sql = "UPDATE [{0}].[Jobs] SET [Payload] = @Payload, [Priority] = @Priority WHERE [Id] = @Id AND [Status] = 0";
        await conn.ExecuteAsync(string.Format(sql, _options.Schema), new { Id = update.JobId, Payload = update.NewPayload, Priority = update.NewPriority });
    }

    public ValueTask<StatsSummaryEntity?> GetSummaryStatsAsync(string queue, CancellationToken ct = default) => throw new NotImplementedException();
    public ValueTask<JobSucceededEntity?> GetSucceededEntityAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
    public ValueTask<JobMorgueEntity?> GetMorgueEntityAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
}