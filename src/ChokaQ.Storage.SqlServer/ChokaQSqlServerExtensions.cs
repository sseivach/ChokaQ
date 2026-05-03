using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Core;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Extension methods for configuring ChokaQ with SQL Server persistence.
/// </summary>
public static class ChokaQSqlServerExtensions
{
    /// <summary>
    /// Configures ChokaQ to use SQL Server as the storage provider.
    /// Implements the Three Pillars architecture: JobsHot, JobsArchive, JobsDLQ.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configuration callback for SQL Server options.</param>
    /// <remarks>
    /// Usage:
    /// <code>
    /// services.AddChokaQ(options => options.AddProfile&lt;MyProfile&gt;());
    /// services.UseSqlServer(options =>
    /// {
    ///     options.ConnectionString = "Server=...";
    ///     options.SchemaName = "chokaq";
    ///     options.AutoCreateSqlTable = true;
    ///     options.PollingInterval = TimeSpan.FromSeconds(5);
    /// });
    /// </code>
    /// 
    /// This method performs the "Great Swap":
    /// 1. Replaces InMemoryJobStorage with SqlJobStorage
    /// 2. Replaces JobWorker with SqlJobWorker (polling-based)
    /// 3. Optionally creates database schema on startup
    /// 
    /// Tables created (when AutoCreateSqlTable = true):
    /// - [schema].[JobsHot]: Active jobs
    /// - [schema].[JobsArchive]: Succeeded jobs history
    /// - [schema].[JobsDLQ]: Failed jobs (dead letter queue)
    /// - [schema].[StatsSummary]: Pre-aggregated metrics
    /// - [schema].[Queues]: Queue configuration
    /// - [schema].[SchemaMigrations]: Applied ChokaQ schema versions
    /// </remarks>
    public static void UseSqlServer(this IServiceCollection services, Action<SqlJobStorageOptions> configure)
    {
        var options = new SqlJobStorageOptions();
        configure(options);

        UseSqlServer(services, options);
    }

    /// <summary>
    /// Configures ChokaQ SQL Server storage from IConfiguration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Either the root configuration, the "ChokaQ" section, or the "ChokaQ:SqlServer" section.
    /// </param>
    /// <param name="configure">
    /// Optional code callback applied after configuration binding. Code wins over appsettings for tests
    /// and deployment-specific overrides.
    /// </param>
    public static void UseSqlServer(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<SqlJobStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SqlJobStorageOptions();
        var rootSqlSection = configuration.GetSection($"{ChokaQOptions.SectionName}:SqlServer");
        var chokaQSqlSection = configuration.GetSection("SqlServer");

        // Accepting root, ChokaQ, or ChokaQ:SqlServer keeps the NuGet API forgiving without
        // creating multiple option models. The resolved values still flow into the single
        // SqlJobStorageOptions type and are validated before workers start.
        if (rootSqlSection.GetChildren().Any() || rootSqlSection.Value is not null)
        {
            rootSqlSection.Bind(options);
        }
        else if (chokaQSqlSection.GetChildren().Any() || chokaQSqlSection.Value is not null)
        {
            chokaQSqlSection.Bind(options);
        }
        else
        {
            configuration.Bind(options);
        }

        configure?.Invoke(options);

        UseSqlServer(services, options);
    }

    private static void UseSqlServer(IServiceCollection services, SqlJobStorageOptions options)
    {
        options.ValidateOrThrow();

        // Register options for DI
        services.Configure<SqlJobStorageOptions>(opt =>
        {
            opt.ConnectionString = options.ConnectionString;
            opt.SchemaName = options.SchemaName;
            opt.AutoCreateSqlTable = options.AutoCreateSqlTable;
            opt.PollingInterval = options.PollingInterval;
            opt.NoQueuesSleepInterval = options.NoQueuesSleepInterval;
            opt.MaxTransientRetries = options.MaxTransientRetries;
            opt.TransientRetryBaseDelayMs = options.TransientRetryBaseDelayMs;
            opt.CommandTimeoutSeconds = options.CommandTimeoutSeconds;
            opt.CleanupBatchSize = options.CleanupBatchSize;
        });

        // =========================================================
        // 1. STORAGE REPLACEMENT
        // =========================================================

        // Remove the default InMemoryJobStorage
        services.RemoveAll<IJobStorage>();

        // Register the SQL Implementation (Three Pillars)
        services.AddSingleton<IJobStorage, SqlJobStorage>();

        // =========================================================
        // 1b. QUEUE PRODUCER REPLACEMENT
        // =========================================================

        // SQL mode must replace the producer as well as the storage. Leaving
        // IChokaQQueue bound to InMemoryQueue would persist the job to SQL and
        // then also write it into an in-process Channel that no SQL worker owns.
        // That creates hidden memory pressure and breaks the durable SQL polling
        // model. The SQL producer commits once, then lets SqlJobWorker poll.
        services.RemoveAll<IChokaQQueue>();
        services.RemoveAll<InMemoryQueue>();

        services.AddSingleton<SqlChokaQQueue>();
        services.AddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<SqlChokaQQueue>());

        // =========================================================
        // 2. WORKER REPLACEMENT (THE SWAP)
        // =========================================================

        // Remove the default JobWorker (listens to In-Memory channels)
        var workerDescriptors = services.Where(d =>
            d.ServiceType == typeof(IHostedService) &&
            (d.ImplementationType == typeof(JobWorkerHostedService) ||
             d.ImplementationType == typeof(JobWorker) ||
             d.ImplementationFactory?.Method.ReturnType == typeof(JobWorker)))
            .ToList();

        foreach (var descriptor in workerDescriptors)
        {
            services.Remove(descriptor);
        }

        services.RemoveAll<JobWorker>();

        // Remove IWorkerManager registration
        services.RemoveAll<IWorkerManager>();

        // Register SqlJobWorker
        services.AddSingleton<SqlJobWorker>(sp =>
        {
            var sqlOptions = sp.GetRequiredService<IOptions<SqlJobStorageOptions>>().Value;
            return new SqlJobWorker(
                sp.GetRequiredService<IJobStorage>(),
                sp.GetRequiredService<ChokaQ.Core.Processing.IJobProcessor>(),
                sp.GetRequiredService<ChokaQ.Core.State.IJobStateManager>(),
                sp.GetRequiredService<ILogger<SqlJobWorker>>(),
                sqlOptions
            );
        });

        // Bind interfaces to SqlJobWorker
        services.AddSingleton<IWorkerManager>(sp => sp.GetRequiredService<SqlJobWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<SqlJobWorker>());

        // =========================================================
        // 3. AUTO-PROVISIONING (MIGRATIONS)
        // =========================================================

        if (options.AutoCreateSqlTable)
        {
            services.AddTransient<SqlInitializer>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<SqlInitializer>>();
                return new SqlInitializer(
                    options.ConnectionString,
                    options.SchemaName,
                    logger,
                    options.CommandTimeoutSeconds);
            });

            services.AddHostedService<DbMigrationWorker>();
        }
    }
}
