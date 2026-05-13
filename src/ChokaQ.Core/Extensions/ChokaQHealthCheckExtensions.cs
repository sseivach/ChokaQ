using ChokaQ.Core;
using ChokaQ.Core.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChokaQ.Core.Extensions;

/// <summary>
/// Extension methods for exposing ChokaQ through the standard .NET health-check pipeline.
/// </summary>
public static class ChokaQHealthCheckExtensions
{
    /// <summary>
    /// Adds ChokaQ worker-liveness and queue-saturation health checks.
    /// </summary>
    /// <remarks>
    /// Use this when the host runs ChokaQ with in-memory storage. SQL Server hosts should prefer
    /// the storage package's AddChokaQSqlServerHealthChecks extension, which adds SQL connectivity
    /// on top of these generic runtime checks.
    /// </remarks>
    public static IHealthChecksBuilder AddChokaQHealthChecks(
        this IHealthChecksBuilder builder,
        Action<ChokaQHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ChokaQHealthCheckOptions();
        configure?.Invoke(options);
        return AddChokaQHealthChecks(builder, options);
    }

    /// <summary>
    /// Adds ChokaQ health checks and binds thresholds from IConfiguration.
    /// </summary>
    /// <param name="configuration">
    /// Either the root configuration, the "ChokaQ" section, or the "ChokaQ:Health" section.
    /// </param>
    /// <param name="configure">
    /// Optional code callback applied after configuration binding. Code wins over appsettings for
    /// local test overrides and deployment-specific thresholds.
    /// </param>
    public static IHealthChecksBuilder AddChokaQHealthChecks(
        this IHealthChecksBuilder builder,
        IConfiguration configuration,
        Action<ChokaQHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = BindHealthOptions(configuration);
        configure?.Invoke(options);
        return AddChokaQHealthChecks(builder, options);
    }

    private static IHealthChecksBuilder AddChokaQHealthChecks(
        IHealthChecksBuilder builder,
        ChokaQHealthCheckOptions options)
    {
        options.ValidateOrThrow();

        builder.Services.Configure<ChokaQHealthCheckOptions>(registered =>
        {
            registered.WorkerHeartbeatTimeout = options.WorkerHeartbeatTimeout;
            registered.QueueLagDegradedThreshold = options.QueueLagDegradedThreshold;
            registered.QueueLagUnhealthyThreshold = options.QueueLagUnhealthyThreshold;
        });

        // Separate checks give operators a useful failure reason. A host with healthy SQL
        // connectivity but a dead worker should page a different owner than a host whose queues
        // are simply saturated by legitimate load.
        builder.AddCheck<ChokaQWorkerHealthCheck>(
            "chokaq_worker",
            tags: new[] { "chokaq", "worker", "live" });

        builder.AddCheck<ChokaQQueueSaturationHealthCheck>(
            "chokaq_queue_saturation",
            tags: new[] { "chokaq", "queue", "saturation" });

        return builder;
    }

    private static ChokaQHealthCheckOptions BindHealthOptions(IConfiguration configuration)
    {
        var options = new ChokaQHealthCheckOptions();
        var rootHealthSection = configuration.GetSection($"{ChokaQOptions.SectionName}:{ChokaQHealthCheckOptions.SectionName}");
        var chokaQHealthSection = configuration.GetSection(ChokaQHealthCheckOptions.SectionName);

        // The overload accepts builder.Configuration, builder.Configuration.GetSection("ChokaQ"),
        // or builder.Configuration.GetSection("ChokaQ:Health"). That keeps health-check setup
        // aligned with AddChokaQ and UseSqlServer without inventing another options model.
        if (rootHealthSection.GetChildren().Any() || rootHealthSection.Value is not null)
        {
            rootHealthSection.Bind(options);
        }
        else if (chokaQHealthSection.GetChildren().Any() || chokaQHealthSection.Value is not null)
        {
            chokaQHealthSection.Bind(options);
        }
        else
        {
            configuration.Bind(options);
        }

        return options;
    }
}
