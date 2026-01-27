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
    // FIX: Renamed to Schema to match our SqlJobStorage implementation
    public string Schema { get; set; } = "chokaq";

    /// <summary>
    /// If true, the library will attempt to create the schema and tables at startup.
    /// </summary>
    public bool AutoCreateSqlTable { get; set; } = false;

    /// <summary>
    /// How often to poll DB for new jobs.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sleep duration when no active queues are detected.
    /// </summary>
    public TimeSpan NoQueuesSleepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// [NEW] Default timeout for jobs before they are considered "Zombies".
    /// Used if Queue-specific timeout is not set.
    /// </summary>
    public int GlobalZombieTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// [NEW] Max number of jobs to fetch in one batch per worker.
    /// </summary>
    public int DefaultBatchSize { get; set; } = 20;
}