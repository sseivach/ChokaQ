using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Production-grade storage implementation using SQL Server and Dapper.
/// Supports Idempotency, Priority Queues, and Atomic Locking via specific T-SQL hints.
/// </summary>
public class SqlJobStorage : IJobStorage
{
    private readonly string _connectionString;
    private readonly ILogger<SqlJobStorage> _logger;
    private readonly string _tableName;

    public SqlJobStorage(
        string connectionString,
        string schemaName,
        ILogger<SqlJobStorage> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new ArgumentException("Schema name cannot be empty.", nameof(schemaName));
        }

        if (!Regex.IsMatch(schemaName, "^[a-zA-Z0-9_]+$"))
        {
            throw new ArgumentException($"Invalid schema name: '{schemaName}'. Only alphanumeric characters and underscores are allowed.");
        }

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
        // Added CreatedBy to SELECT
        var sql = $@"
            SELECT Id, Queue, Type, Payload, Status, AttemptCount, 
                   Priority, ScheduledAtUtc, Tags, IdempotencyKey, WorkerId, ErrorDetails, CreatedBy,
                   CreatedAtUtc, StartedAtUtc, FinishedAtUtc, LastUpdatedUtc
            FROM {_tableName}
            WHERE Id = @Id";

        using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<JobStorageDto>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
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
        // Added CreatedBy to SELECT
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
        CancellationToken ct = default)
    {
        // Added CreatedBy to OUTPUT INSERTED
        var sql = $@"
            WITH SortedJobs AS (
                SELECT TOP (@Limit) Id
                FROM {_tableName} WITH (ROWLOCK, READPAST, UPDLOCK)
                WHERE Status = 0 -- Pending
                  AND (ScheduledAtUtc IS NULL OR ScheduledAtUtc <= @Now)
                ORDER BY Priority DESC, ScheduledAtUtc ASC, CreatedAtUtc ASC
            )
            UPDATE J
            SET 
                Status = 1, -- Processing
                WorkerId = @WorkerId,
                StartedAtUtc = @Now,
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
            Now = DateTime.UtcNow
        }, cancellationToken: ct));
    }
}