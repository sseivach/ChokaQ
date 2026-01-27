using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChokaQ.Storage.SqlServer;

public static class ChokaQSqlServerExtensions
{
    /// <summary>
    /// Configures ChokaQ to use SQL Server as the storage provider (Three Pillars architecture).
    /// Replaces the default In-Memory storage and In-Memory Worker with SQL-backed implementations.
    /// </summary>
    public static void UseSqlServer(this IServiceCollection services, Action<SqlJobStorageOptions> configure)
    {
        var options = new SqlJobStorageOptions();
        configure(options);

        // Validation
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentNullException(nameof(options.ConnectionString), "Connection string cannot be empty.");
        }

        // Register options for DI
        services.Configure<SqlJobStorageOptions>(opt =>
        {
            opt.ConnectionString = options.ConnectionString;
            opt.SchemaName = options.SchemaName;
            opt.AutoCreateSqlTable = options.AutoCreateSqlTable;
            opt.PollingInterval = options.PollingInterval;
            opt.NoQueuesSleepInterval = options.NoQueuesSleepInterval;
        });

        // =========================================================
        // 1. STORAGE REPLACEMENT
        // =========================================================

        // Remove the default InMemoryJobStorage
        services.RemoveAll<IJobStorage>();

        // Register the SQL Implementation (Three Pillars)
        services.AddSingleton<IJobStorage, SqlJobStorage>();

        // =========================================================
        // 2. WORKER REPLACEMENT (THE SWAP)
        // =========================================================

        // Remove the default JobWorker (listens to In-Memory channels)
        var workerDescriptors = services.Where(d =>
            d.ServiceType == typeof(IHostedService) &&
            (d.ImplementationType == typeof(JobWorker) ||
             d.ImplementationFactory?.Method.ReturnType == typeof(JobWorker)))
            .ToList();

        foreach (var descriptor in workerDescriptors)
        {
            services.Remove(descriptor);
        }

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
                return new SqlInitializer(options.ConnectionString, options.SchemaName, logger);
            });

            services.AddHostedService<DbMigrationWorker>();
        }
    }
}
