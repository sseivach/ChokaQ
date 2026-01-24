using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace ChokaQ.Storage.SqlServer;

public class SqlJobStorage : IJobStorage
{
    private readonly string _connectionString;
    private readonly string _schemaName;
    private readonly string _tableName;
    private readonly ILogger<SqlJobStorage> _logger;

    public SqlJobStorage(
        string connectionString,
        string schemaName,
        ILogger<SqlJobStorage> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(schemaName))
            throw new ArgumentException("Schema name cannot be empty.", nameof(schemaName));

        if (!Regex.IsMatch(schemaName, "^[a-zA-Z0-9_]+$"))
            throw new ArgumentException($"Invalid schema name: '{schemaName}'. Only alphanumeric characters and underscores are allowed.");

        _schemaName = schemaName;
        _tableName = $"[{schemaName}].[Jobs]";
    }

    /// <inheritdoc />
    public async ValueTask<string> CreateJobAsync(
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
        var sql = $@"
            INSERT INTO {_tableName} 
            (Id, Queue, Type, Payload, Status, AttemptCount, Priority, CreatedBy, Tags, IdempotencyKey, CreatedAtUtc, ScheduledAtUtc, LastUpdatedUtc)
            VALUES 
            (@Id, @Queue, @Type, @Payload, @Status, @AttemptCount, @Priority, @CreatedBy, @Tags, @IdempotencyKey, @CreatedAtUtc, @ScheduledAtUtc, @LastUpdatedUtc)";

        var now = DateTime.UtcNow;
        DateTime? scheduledAt = delay.HasValue ? now.Add(delay.Value) : null;

        var parameters = new
        {
            Id = id,
            Queue = queue,
            Type = jobType,
            Payload = payload,
            Status = (int)JobStatus.Pending,
            AttemptCount = 0,
            Priority = priority,
            CreatedBy = createdBy,
            Tags = tags,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now,
            ScheduledAtUtc = scheduledAt,
            LastUpdatedUtc = now
        };

        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
            return id;
        }
        catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
        {
            _logger.LogInformation("Job ID collision or Idempotency hit for ID: {JobId}. Returning existing ID.", id);
            return id;
        }
    }

    /// <inheritdoc />
    public async ValueTask<JobStorageDto?> GetJobAsync(string id, CancellationToken ct = default)
    {
        var sql = $@"
            SELECT Id, Queue, Type, Payload, Status, AttemptCount, 
                   Priority, ScheduledAtUtc, Tags, IdempotencyKey, WorkerId, ErrorDetails, CreatedBy,
                   CreatedAtUtc, StartedAtUtc, FinishedAtUtc, LastUpdatedUtc
            FROM {_tableName}
            WHERE Id = @Id";

        using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<JobStorageDto>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async ValueTask<JobCountsDto> GetJobCountsAsync(CancellationToken ct = default)
    {
        var sql = $@"
            SELECT Status, COUNT(*) as Count 
            FROM {_tableName} 
            GROUP BY Status";

        using var connection = new SqlConnection(_connectionString);
        var rows = await connection.QueryAsync<(int Status, int Count)>(new CommandDefinition(sql, cancellationToken: ct));

        int pending = 0, fetched = 0, processing = 0, succeeded = 0, failed = 0, cancelled = 0;

        foreach (var row in rows)
        {
            switch ((JobStatus)row.Status)
            {
                case JobStatus.Pending: pending += row.Count; break;
                case JobStatus.Fetched: fetched += row.Count; break;
                case JobStatus.Processing: processing += row.Count; break;
                case JobStatus.Succeeded: succeeded += row.Count; break;
                case JobStatus.Failed: failed += row.Count; break;
                case JobStatus.Cancelled: cancelled += row.Count; break;
            }
        }

        return new JobCountsDto(pending, fetched, processing, succeeded, failed, cancelled,
            pending + fetched + processing + succeeded + failed + cancelled);
    }

    /// <inheritdoc />
    public async ValueTask<bool> UpdateJobStateAsync(string id, JobStatus status, CancellationToken ct = default)
    {
        var sql = $@"
            UPDATE {_tableName}
            SET Status = @Status, LastUpdatedUtc = @Now
            WHERE Id = @Id";

        using var connection = new SqlConnection(_connectionString);
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = id,
            Status = (int)status,
            Now = DateTime.UtcNow
        }, cancellationToken: ct));

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IncrementJobAttemptAsync(string id, int newAttemptCount, CancellationToken ct = default)
    {
        var sql = $@"
            UPDATE {_tableName}
            SET AttemptCount = @Count, LastUpdatedUtc = @Now
            WHERE Id = @Id";

        using var connection = new SqlConnection(_connectionString);
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = id,
            Count = newAttemptCount,
            Now = DateTime.UtcNow
        }, cancellationToken: ct));

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<IEnumerable<JobStorageDto>> GetJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        var sql = $@"
            SELECT TOP (@Limit) 
                   Id, Queue, Type, Payload, Status, AttemptCount, 
                   Priority, ScheduledAtUtc, Tags, IdempotencyKey, WorkerId, ErrorDetails, CreatedBy,
                   CreatedAtUtc, StartedAtUtc, FinishedAtUtc, LastUpdatedUtc
            FROM {_tableName}
            ORDER BY CreatedAtUtc DESC";

        using var connection = new SqlConnection(_connectionString);
        return await connection.QueryAsync<JobStorageDto>(new CommandDefinition(sql, new { Limit = limit }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async ValueTask<IEnumerable<JobStorageDto>> FetchAndLockNextBatchAsync(
        string workerId,
        int limit,
        string[]? allowedQueues,
        CancellationToken ct = default)
    {
        if (allowedQueues == null || allowedQueues.Length == 0)
            return Enumerable.Empty<JobStorageDto>();

        // Logic:
        // 1. Find Pending (0)
        // 2. Set to FETCHED (1) -> Not Processing (2) yet!
        var sql = $@"
            WITH SortedJobs AS (
                SELECT TOP (@Limit) Id
                FROM {_tableName} WITH (ROWLOCK, READPAST, UPDLOCK)
                WHERE Status = @PendingStatus
                  AND (ScheduledAtUtc IS NULL OR ScheduledAtUtc <= @Now)
                  AND Queue IN @AllowedQueues
                ORDER BY Priority DESC, ScheduledAtUtc ASC, CreatedAtUtc ASC
            )
            UPDATE J
            SET 
                Status = @FetchedStatus, -- <--- SET TO 1
                WorkerId = @WorkerId,
                LastUpdatedUtc = @Now,
                AttemptCount = AttemptCount + 1
            OUTPUT 
                INSERTED.Id, INSERTED.Queue, INSERTED.Type, INSERTED.Payload, 
                INSERTED.Status, INSERTED.AttemptCount, 
                INSERTED.Priority, INSERTED.ScheduledAtUtc, INSERTED.Tags, 
                INSERTED.IdempotencyKey, INSERTED.WorkerId, INSERTED.ErrorDetails, INSERTED.CreatedBy,
                INSERTED.CreatedAtUtc, INSERTED.StartedAtUtc, INSERTED.FinishedAtUtc, INSERTED.LastUpdatedUtc
            FROM {_tableName} J
            INNER JOIN SortedJobs SJ ON J.Id = SJ.Id";

        using var connection = new SqlConnection(_connectionString);
        return await connection.QueryAsync<JobStorageDto>(new CommandDefinition(sql, new
        {
            Limit = limit,
            WorkerId = workerId,
            Now = DateTime.UtcNow,
            AllowedQueues = allowedQueues,
            PendingStatus = JobStatus.Pending,
            FetchedStatus = JobStatus.Fetched
        }, cancellationToken: ct));
    }

    public async Task MarkAsProcessingAsync(string jobId, CancellationToken ct)
    {
        // Transition from Fetched (1) to Processing (2) + Set Start Time
        var sql = $@"
            UPDATE {_tableName}
            SET 
                Status = @ProcessingStatus,
                StartedAtUtc = SYSUTCDATETIME(),
                LastUpdatedUtc = SYSUTCDATETIME()
            WHERE Id = @Id";

        using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = jobId,
            ProcessingStatus = JobStatus.Processing // 2
        }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default)
    {
        var queuesTable = $"[{_schemaName}].[Queues]";
        var jobsTable = $"[{_schemaName}].[Jobs]";

        var sql = $@"
            WITH JobStats AS (
                SELECT 
                    Queue,
                    COUNT(CASE WHEN Status = 0 THEN 1 END) as PendingCount,
                    COUNT(CASE WHEN Status = 1 THEN 1 END) as FetchedCount,    -- <--- NEW COLUMN
                    COUNT(CASE WHEN Status = 2 THEN 1 END) as ProcessingCount,
                    COUNT(CASE WHEN Status = 3 THEN 1 END) as SucceededCount,
                    COUNT(CASE WHEN Status = 4 THEN 1 END) as FailedCount,
                    MIN(StartedAtUtc) as FirstJobAtUtc,
                    MAX(FinishedAtUtc) as LastJobAtUtc
                FROM {jobsTable}
                GROUP BY Queue
            )
            SELECT 
                COALESCE(Q.Name, JS.Queue) as Name,
                CAST(COALESCE(Q.IsPaused, 0) AS BIT) as IsPaused,
                ISNULL(JS.PendingCount, 0) as PendingCount,
                ISNULL(JS.FetchedCount, 0) as FetchedCount,       -- <--- Mapped
                ISNULL(JS.ProcessingCount, 0) as ProcessingCount,
                ISNULL(JS.FailedCount, 0) as FailedCount,
                ISNULL(JS.SucceededCount, 0) as SucceededCount,
                JS.FirstJobAtUtc,
                JS.LastJobAtUtc
            FROM {queuesTable} Q
            FULL OUTER JOIN JobStats JS ON JS.Queue = Q.Name
            ORDER BY JS.LastJobAtUtc DESC";

        using var connection = new SqlConnection(_connectionString);
        return await connection.QueryAsync<QueueDto>(new CommandDefinition(sql, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        var queuesTable = $"[{_schemaName}].[Queues]";

        // FIX: Use MERGE (Upsert). 
        // If the queue doesn't exist in the [Queues] table (it was dynamically created by a job),
        // UPDATE would fail to find it. MERGE creates it.
        var sql = $@"
            MERGE {queuesTable} AS target
            USING (SELECT @Name AS Name) AS source
            ON (target.Name = source.Name)
            WHEN MATCHED THEN
                UPDATE SET IsPaused = @IsPaused, LastUpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (Name, IsPaused, LastUpdatedUtc)
                VALUES (@Name, @IsPaused, SYSUTCDATETIME());";

        using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Name = queueName,
            IsPaused = isPaused
        }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async ValueTask UpdateJobPriorityAsync(string id, int newPriority, CancellationToken ct = default)
    {
        var sql = $@"
            UPDATE {_tableName}
            SET Priority = @Priority, LastUpdatedUtc = SYSUTCDATETIME()
            WHERE Id = @Id";

        using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = id, Priority = newPriority }, cancellationToken: ct));
    }
}