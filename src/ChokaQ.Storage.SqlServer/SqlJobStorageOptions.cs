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
}