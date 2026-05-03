namespace ChokaQ.Storage.SqlServer;

public class SqlJobStorageOptions
{
    /// <summary>
    /// The connection string to the SQL Server database.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The schema name where tables and procedures will be created.
    /// Default: "chokaq".
    /// </summary>
    public string SchemaName { get; set; } = "chokaq";

    /// <summary>
    /// If true, the library will attempt to create the schema and tables at startup.
    /// WARNING: Requires 'CREATE SCHEMA' and 'CREATE TABLE' permissions.
    /// Recommended for Development, but use with caution in Production.
    /// Default: false.
    /// </summary>
    public bool AutoCreateSqlTable { get; set; } = false;

    /// <summary>
    /// The interval at which the worker polls the database for new jobs when queues are active.
    /// Default: 1 second.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The interval to sleep when no active queues are found.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan NoQueuesSleepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of retries for transient database errors (e.g., deadlocks, network blips).
    /// Default: 3.
    /// </summary>
    public int MaxTransientRetries { get; set; } = 3;

    /// <summary>
    /// Base delay before retrying a failed database operation. Grows exponentially.
    /// Default: 200 milliseconds.
    /// </summary>
    public int TransientRetryBaseDelayMs { get; set; } = 200;

    /// <summary>
    /// Maximum time each individual SQL command may run before ADO.NET cancels it.
    /// Default: 30 seconds.
    /// </summary>
    /// <remarks>
    /// This protects workers from hanging forever behind blocking, missing indexes, or network
    /// stalls. It is intentionally separate from job execution timeout: one controls database
    /// calls made by ChokaQ, the other controls user handler code.
    /// </remarks>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of rows deleted by one cleanup transaction.
    /// Default: 1000.
    /// </summary>
    /// <remarks>
    /// Retention cleanup is intentionally batched. Large one-shot DELETE statements can hold
    /// locks for a long time, grow the transaction log, and make the dashboard or workers wait
    /// behind maintenance work. A bounded batch size lets operators tune cleanup pressure for
    /// their SQL Server tier: smaller batches are gentler, larger batches finish retention faster.
    /// </remarks>
    public int CleanupBatchSize { get; set; } = 1000;

    /// <summary>
    /// Throws a startup exception when SQL storage configuration is unsafe or incomplete.
    /// </summary>
    /// <remarks>
    /// SQL settings control the durable boundary of the queue. Failing fast on an empty
    /// connection string or non-positive polling interval is much cheaper than starting a
    /// worker that cannot persist, fetch, or recover jobs correctly.
    /// </remarks>
    public void ValidateOrThrow()
    {
        var errors = Validate();

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid ChokaQ SQL Server configuration: " + string.Join("; ", errors));
        }
    }

    /// <summary>
    /// Returns all validation errors for diagnostics and tests.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            errors.Add("ConnectionString cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(SchemaName))
        {
            errors.Add("SchemaName cannot be empty.");
        }

        if (PollingInterval <= TimeSpan.Zero)
        {
            errors.Add("PollingInterval must be greater than zero.");
        }

        if (NoQueuesSleepInterval <= TimeSpan.Zero)
        {
            errors.Add("NoQueuesSleepInterval must be greater than zero.");
        }

        if (MaxTransientRetries < 0)
        {
            errors.Add("MaxTransientRetries must not be negative.");
        }

        if (TransientRetryBaseDelayMs <= 0)
        {
            errors.Add("TransientRetryBaseDelayMs must be greater than zero.");
        }

        if (CommandTimeoutSeconds <= 0)
        {
            errors.Add("CommandTimeoutSeconds must be greater than zero.");
        }

        if (CleanupBatchSize <= 0)
        {
            errors.Add("CleanupBatchSize must be greater than zero.");
        }

        return errors;
    }
}
