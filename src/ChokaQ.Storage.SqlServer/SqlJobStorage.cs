using System.Data;
using System.Text.RegularExpressions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Resilience;
using ChokaQ.Abstractions.Storage;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Storage.SqlServer;

public class SqlJobStorage : IJobStorage
{
    private readonly string _connectionString;
    private readonly string _schemaName;
    private readonly ILogger<SqlJobStorage> _logger;
    private readonly IDeduplicator _deduplicator;

    // Helpers
    private string TableJobs => $"[{_schemaName}].[Jobs]";
    private string TableArchive => $"[{_schemaName}].[JobsSucceeded]";
    private string TableMorgue => $"[{_schemaName}].[JobsMorgue]";
    private string TableStats => $"[{_schemaName}].[StatsSummary]";
    private string TableQueues => $"[{_schemaName}].[Queues]";

    public SqlJobStorage(
        string connectionString,
        string schemaName,
        ILogger<SqlJobStorage> logger,
        IDeduplicator deduplicator)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deduplicator = deduplicator ?? throw new ArgumentNullException(nameof(deduplicator));

        if (!Regex.IsMatch(schemaName, "^[a-zA-Z0-9_]+$"))
            throw new ArgumentException($"Invalid schema name: '{schemaName}'");

        _schemaName = schemaName;
    }

    private SqlConnection GetConnection() => new(_connectionString);

    // --- 1. Production Flow ---

    public async ValueTask CreateJobAsync(JobStorageDto job, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(job.IdempotencyKey))
        {
            if (!await _deduplicator.TryAcquireAsync(job.IdempotencyKey, TimeSpan.FromMinutes(10)))
            {
                _logger.LogInformation("🛡️ Deduplicator: Blocked duplicate job '{Key}'", job.IdempotencyKey);
                return;
            }
        }

        var sql = $@"
            INSERT INTO {TableJobs} 
            (Id, Queue, Type, Payload, Priority, Status, Tags, IdempotencyKey, CreatedAtUtc, ScheduledAtUtc, AttemptCount, LastUpdatedUtc)
            VALUES 
            (@Id, @Queue, @Type, @Payload, @Priority, @Status, @Tags, @IdempotencyKey, @CreatedAtUtc, @ScheduledAtUtc, @AttemptCount, SYSUTCDATETIME())";

        try
        {
            using var conn = GetConnection();
            await conn.ExecuteAsync(new CommandDefinition(sql, job, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            _logger.LogInformation("Job ID collision: {JobId}", job.Id);
        }
    }

    public async ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(string workerId, int limit, string[]? allowedQueues, CancellationToken ct = default)
    {
        if (allowedQueues == null || allowedQueues.Length == 0) return Enumerable.Empty<JobStorageDto>();

        var sql = $@"
            WITH SortedJobs AS (
                SELECT TOP (@Limit) Id
                FROM {TableJobs} WITH (ROWLOCK, READPAST, UPDLOCK)
                WHERE Status = @PendingStatus
                  AND (ScheduledAtUtc IS NULL OR ScheduledAtUtc <= SYSUTCDATETIME())
                  AND Queue IN @AllowedQueues
                ORDER BY Priority DESC, ScheduledAtUtc ASC, CreatedAtUtc ASC
            )
            UPDATE J
            SET Status = @ProcessingStatus,
                WorkerId = @WorkerId,
                FetchedAtUtc = SYSUTCDATETIME(),
                StartedAtUtc = SYSUTCDATETIME(),
                HeartbeatUtc = SYSUTCDATETIME(),
                LastUpdatedUtc = SYSUTCDATETIME(),
                AttemptCount = AttemptCount + 1
            OUTPUT INSERTED.*
            FROM {TableJobs} J
            INNER JOIN SortedJobs SJ ON J.Id = SJ.Id";

        using var conn = GetConnection();
        return await conn.QueryAsync<JobStorageDto>(new CommandDefinition(sql, new
        {
            Limit = limit,
            WorkerId = workerId,
            AllowedQueues = allowedQueues,
            PendingStatus = (int)JobStatus.Pending,
            ProcessingStatus = (int)JobStatus.Processing
        }, cancellationToken: ct));
    }

    public async ValueTask UpdateHeartbeatAsync(string jobId, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE {TableJobs} SET HeartbeatUtc = SYSUTCDATETIME(), LastUpdatedUtc = SYSUTCDATETIME() WHERE Id = @Id",
            new { Id = jobId }, cancellationToken: ct));
    }

    public async ValueTask ArchiveJobAsync(string jobId, JobStatus finalStatus, string? error = null, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        await conn.OpenAsync(ct);
        using var transaction = conn.BeginTransaction();

        try
        {
            if (finalStatus == JobStatus.Succeeded)
            {
                var moveSql = $@"
                    INSERT INTO {TableArchive} (Id, Queue, Type, Payload, Tags, FinishedAtUtc, DurationMs, CreatedBy)
                    SELECT Id, Queue, Type, Payload, Tags, SYSUTCDATETIME(), 
                           DATEDIFF(MILLISECOND, FetchedAtUtc, SYSUTCDATETIME()), NULL
                    FROM {TableJobs} WHERE Id = @Id;

                    DELETE FROM {TableJobs} WHERE Id = @Id;

                    UPDATE {TableStats} SET SucceededTotal = SucceededTotal + 1, LastUpdatedUtc = SYSUTCDATETIME() 
                    WHERE QueueName = (SELECT Queue FROM {TableArchive} WHERE Id = @Id);";

                await conn.ExecuteAsync(moveSql, new { Id = jobId }, transaction);
            }
            else
            {
                var morgueSql = $@"
                    INSERT INTO {TableMorgue} (Id, Queue, Type, Payload, Tags, ErrorDetails, AttemptCount, FailedAtUtc)
                    SELECT Id, Queue, Type, Payload, Tags, @Error, AttemptCount, SYSUTCDATETIME()
                    FROM {TableJobs} WHERE Id = @Id;

                    DELETE FROM {TableJobs} WHERE Id = @Id;

                    UPDATE {TableStats} SET FailedTotal = FailedTotal + 1, LastUpdatedUtc = SYSUTCDATETIME()
                    WHERE QueueName = (SELECT Queue FROM {TableMorgue} WHERE Id = @Id);";

                await conn.ExecuteAsync(morgueSql, new { Id = jobId, Error = error }, transaction);
            }
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async ValueTask ScheduleRetryAsync(string jobId, DateTime nextAttemptUtc, int attemptCount, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        var sql = $@"
            UPDATE {TableJobs} 
            SET Status = @Status, ScheduledAtUtc = @NextAttempt, WorkerId = NULL, AttemptCount = @AttemptCount, LastUpdatedUtc = SYSUTCDATETIME()
            WHERE Id = @Id";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = jobId,
            Status = (int)JobStatus.Pending,
            NextAttempt = nextAttemptUtc,
            AttemptCount = attemptCount
        }, cancellationToken: ct));
    }

    // --- 2. Resilience ---

    public async ValueTask<int> RescueZombiesAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        var sql = $@"
            UPDATE J
            SET Status = @Pending, WorkerId = NULL, HeartbeatUtc = NULL, 
                ErrorDetails = 'Zombie Rescued', LastUpdatedUtc = SYSUTCDATETIME()
            FROM {TableJobs} J
            LEFT JOIN {TableQueues} Q ON J.Queue = Q.Name
            WHERE J.Status = @Processing 
              AND J.HeartbeatUtc < DATEADD(second, -COALESCE(Q.ZombieTimeoutSeconds, @GlobalTimeout), SYSUTCDATETIME())";

        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Pending = (int)JobStatus.Pending,
            Processing = (int)JobStatus.Processing,
            GlobalTimeout = (int)timeout.TotalSeconds
        }, cancellationToken: ct));
    }

    public async ValueTask ResurrectJobAsync(string jobId, string? newPayload = null, string? newTags = null, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        await conn.OpenAsync(ct);
        using var transaction = conn.BeginTransaction();
        try
        {
            var sql = $@"
                INSERT INTO {TableJobs} 
                (Id, Queue, Type, Payload, Priority, Status, Tags, CreatedAtUtc, AttemptCount, LastUpdatedUtc)
                SELECT Id, Queue, Type, COALESCE(@Payload, Payload), 10, @PendingStatus, COALESCE(@Tags, Tags), SYSUTCDATETIME(), 0, SYSUTCDATETIME()
                FROM {TableMorgue} WHERE Id = @Id;
                DELETE FROM {TableMorgue} WHERE Id = @Id;";

            var affected = await conn.ExecuteAsync(sql, new
            {
                Id = jobId,
                Payload = newPayload,
                Tags = newTags,
                PendingStatus = (int)JobStatus.Pending
            }, transaction);

            if (affected == 0) throw new InvalidOperationException($"Job {jobId} not found in Morgue.");
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    // --- 3. Read Models ---

    public async ValueTask<JobStorageDto?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        return await conn.QuerySingleOrDefaultAsync<JobStorageDto>(
            new CommandDefinition($"SELECT * FROM {TableJobs} WHERE Id = @Id", new { Id = jobId }, cancellationToken: ct));
    }

    public async ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(JobStatus status, int page, int pageSize, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        var (tableName, sortColumn) = status switch
        {
            JobStatus.Succeeded => (TableArchive, "FinishedAtUtc"),
            JobStatus.Failed => (TableMorgue, "FailedAtUtc"),
            _ => (TableJobs, "CreatedAtUtc")
        };
        var whereClause = tableName == TableJobs ? "WHERE Status = @Status" : "WHERE 1=1";
        var sql = $@"
            SELECT * FROM {tableName} {whereClause}
            ORDER BY {sortColumn} DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        return await conn.QueryAsync<JobStorageDto>(new CommandDefinition(sql, new
        {
            Status = (int)status,
            Offset = (page - 1) * pageSize,
            PageSize = pageSize
        }, cancellationToken: ct));
    }

    public async ValueTask<IEnumerable<QueueStatsDto>> GetSummaryStatsAsync(CancellationToken ct = default)
    {
        using var conn = GetConnection();
        return await conn.QueryAsync<QueueStatsDto>(new CommandDefinition($"SELECT * FROM {TableStats}", cancellationToken: ct));
    }

    public async ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default)
    {
        var sql = $@"
            WITH JobStats AS (
                SELECT Queue,
                    COUNT(CASE WHEN Status = 0 THEN 1 END) as PendingCount,
                    COUNT(CASE WHEN Status = 2 THEN 1 END) as ProcessingCount,
                    MIN(StartedAtUtc) as FirstJobAtUtc,
                    MAX(CreatedAtUtc) as LastJobAtUtc
                FROM {TableJobs} WITH (NOLOCK) GROUP BY Queue
            )
            SELECT COALESCE(Q.Name, JS.Queue) as Name, CAST(COALESCE(Q.IsPaused, 0) AS BIT) as IsPaused,
                ISNULL(JS.PendingCount, 0) as PendingCount, ISNULL(JS.ProcessingCount, 0) as ProcessingCount,
                0 as FetchedCount, 0 as SucceededCount, 0 as FailedCount, 0 as CancelledCount,
                Q.ZombieTimeoutSeconds, JS.FirstJobAtUtc, JS.LastJobAtUtc
            FROM {TableQueues} Q WITH (NOLOCK) FULL OUTER JOIN JobStats JS ON JS.Queue = Q.Name
            ORDER BY Name ASC";
        using var conn = GetConnection();
        return await conn.QueryAsync<QueueDto>(new CommandDefinition(sql, cancellationToken: ct));
    }

    // --- 4. Admin Ops ---

    public async ValueTask UpdatePayloadAsync(string jobId, string payload, string? tags = null, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        var sql = $"UPDATE {TableJobs} SET Payload = @Payload, Tags = COALESCE(@Tags, Tags), LastUpdatedUtc = SYSUTCDATETIME() WHERE Id = @Id AND Status = 0";
        var res = await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = jobId, Payload = payload, Tags = tags }, cancellationToken: ct));
        if (res == 0) throw new InvalidOperationException("Job not found or not Pending.");
    }

    public async ValueTask UpdateJobPriorityAsync(string id, int newPriority, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        await conn.ExecuteAsync(new CommandDefinition($"UPDATE {TableJobs} SET Priority = @P, LastUpdatedUtc = SYSUTCDATETIME() WHERE Id = @Id", new { Id = id, P = newPriority }, cancellationToken: ct));
    }

    public async ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        var sql = $@"MERGE {TableQueues} AS T USING (SELECT @Name AS Name) AS S ON (T.Name = S.Name)
                     WHEN MATCHED THEN UPDATE SET IsPaused = @IsPaused, LastUpdatedUtc = SYSUTCDATETIME()
                     WHEN NOT MATCHED THEN INSERT (Name, IsPaused) VALUES (@Name, @IsPaused);";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Name = queueName, IsPaused = isPaused }, cancellationToken: ct));
    }

    public async ValueTask UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default)
    {
        using var conn = GetConnection();
        var sql = $@"MERGE {TableQueues} AS T USING (SELECT @Name AS Name) AS S ON (T.Name = S.Name)
                     WHEN MATCHED THEN UPDATE SET ZombieTimeoutSeconds = @T, LastUpdatedUtc = SYSUTCDATETIME()
                     WHEN NOT MATCHED THEN INSERT (Name, IsPaused, ZombieTimeoutSeconds) VALUES (@Name, 0, @T);";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Name = queueName, T = timeoutSeconds }, cancellationToken: ct));
    }
}