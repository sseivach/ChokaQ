using ChokaQ.Core.Extensions;
using ChokaQ.Core.Health;
using ChokaQ.Storage.SqlServer.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Extension methods for SQL Server-backed ChokaQ health checks.
/// </summary>
public static class ChokaQSqlServerHealthCheckExtensions
{
    /// <summary>
    /// Adds SQL connectivity, worker-liveness, and queue-saturation health checks.
    /// </summary>
    /// <remarks>
    /// Recommended host usage:
    /// <code>
    /// builder.Services.AddHealthChecks()
    ///     .AddChokaQSqlServerHealthChecks();
    /// </code>
    ///
    /// The three checks intentionally remain separate so orchestrators and dashboards can show
    /// the real failing layer: database boundary, local worker loop, or queue saturation.
    /// </remarks>
    public static IHealthChecksBuilder AddChokaQSqlServerHealthChecks(
        this IHealthChecksBuilder builder,
        Action<ChokaQHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddChokaQHealthChecks(configure);
        AddSqlConnectivityCheck(builder);

        return builder;
    }

    /// <summary>
    /// Adds SQL Server-backed ChokaQ health checks and binds thresholds from IConfiguration.
    /// </summary>
    /// <param name="configuration">
    /// Either the root configuration, the "ChokaQ" section, or the "ChokaQ:Health" section.
    /// </param>
    public static IHealthChecksBuilder AddChokaQSqlServerHealthChecks(
        this IHealthChecksBuilder builder,
        IConfiguration configuration,
        Action<ChokaQHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.AddChokaQHealthChecks(configuration, configure);
        AddSqlConnectivityCheck(builder);

        return builder;
    }

    private static void AddSqlConnectivityCheck(IHealthChecksBuilder builder)
    {
        builder.AddCheck<ChokaQSqlConnectivityHealthCheck>(
            "chokaq_sql",
            tags: new[] { "chokaq", "sql", "storage" });
    }
}
