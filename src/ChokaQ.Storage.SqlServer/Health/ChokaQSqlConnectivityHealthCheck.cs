using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ChokaQ.Storage.SqlServer.Health;

/// <summary>
/// Verifies that the SQL Server storage boundary is reachable and initialized.
/// </summary>
/// <remarks>
/// Connectivity alone is not enough for a durable queue. The process must be able to open a
/// connection and see the ChokaQ schema/tables that workers depend on. This check deliberately
/// uses a tiny metadata query so it stays cheap enough for frequent platform probes.
/// </remarks>
internal sealed class ChokaQSqlConnectivityHealthCheck : IHealthCheck
{
    private readonly SqlJobStorageOptions _options;

    public ChokaQSqlConnectivityHealthCheck(IOptions<SqlJobStorageOptions> options)
    {
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new SqlConnection(_options.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.CommandText = @"
                SELECT CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM sys.tables t
                        INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                        WHERE s.[name] = @SchemaName
                          AND t.[name] IN ('JobsHot', 'JobsArchive', 'JobsDLQ', 'StatsSummary', 'Queues')
                        GROUP BY s.[name]
                        HAVING COUNT(DISTINCT t.[name]) = 5
                    )
                    THEN 1 ELSE 0 END;";

            cmd.Parameters.AddWithValue("@SchemaName", _options.SchemaName);
            var initialized = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) == 1;

            var data = new Dictionary<string, object>
            {
                ["schema"] = _options.SchemaName,
                ["commandTimeoutSeconds"] = _options.CommandTimeoutSeconds
            };

            return initialized
                ? HealthCheckResult.Healthy("ChokaQ SQL Server storage is reachable and initialized.", data)
                : HealthCheckResult.Unhealthy(
                    $"ChokaQ SQL Server schema '{_options.SchemaName}' is reachable but missing required tables.",
                    data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(
                "ChokaQ SQL Server storage is not reachable.",
                ex,
                new Dictionary<string, object>
                {
                    ["schema"] = _options.SchemaName,
                    ["commandTimeoutSeconds"] = _options.CommandTimeoutSeconds
                });
        }
    }
}
