using ChokaQ.Abstractions;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Storage.SqlServer;

public static class ChokaQSqlServerExtensions
{
    /// <summary>
    /// Configures ChokaQ to use SQL Server as the storage provider.
    /// This method replaces the default In-Memory storage and In-Memory Worker with SQL-backed implementations.
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

        // =========================================================
        // 1. STORAGE REPLACEMENT
        // =========================================================

        // Remove the default InMemoryJobStorage
        services.RemoveAll<IJobStorage>();

        // Register the SQL Implementation
        services.AddSingleton<IJobStorage>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SqlJobStorage>>();
            return new SqlJobStorage(options.ConnectionString, options.SchemaName, logger);
        });

        // =========================================================
        // 2. WORKER REPLACEMENT (THE SWAP)
        // =========================================================

        // We need to remove the default JobWorker because it listens to In-Memory channels.
        // We want SqlJobWorker which polls the database.

        // A. Remove the Hosted Service registration for JobWorker
        // Since JobWorker is often registered via a factory (sp => ...), ImplementationType might be null.
        // We check the factory's ReturnType to identify it correctly.
        var workerDescriptors = services.Where(d =>
            d.ServiceType == typeof(IHostedService) &&
            (d.ImplementationType == typeof(JobWorker) ||
             d.ImplementationFactory?.Method.ReturnType == typeof(JobWorker)))
            .ToList();

        foreach (var descriptor in workerDescriptors)
        {
            services.Remove(descriptor);
        }

        // B. Remove the IWorkerManager registration (which points to the old worker)
        services.RemoveAll<IWorkerManager>();

        // C. Register SqlJobWorker
        // We register it as a Singleton first, so we can reference it in multiple interfaces
        services.AddSingleton<SqlJobWorker>(sp =>
        {
            return new SqlJobWorker(
                sp.GetRequiredService<IJobStorage>(),
                sp.GetRequiredService<ChokaQ.Core.Processing.IJobProcessor>(),
                sp.GetRequiredService<ChokaQ.Core.State.IJobStateManager>(),
                sp.GetRequiredService<ILogger<SqlJobWorker>>(),
                options
            );
        });

        // D. Bind interfaces to the SqlJobWorker
        services.AddSingleton<IWorkerManager>(sp => sp.GetRequiredService<SqlJobWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<SqlJobWorker>());

        // =========================================================
        // 3. AUTO-PROVISIONING (MIGRATIONS)
        // =========================================================

        if (options.AutoCreateSqlTable)
        {
            // Register the Initializer (Transient is fine, used once)
            services.AddTransient<SqlInitializer>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<SqlInitializer>>();
                return new SqlInitializer(options.ConnectionString, options.SchemaName, logger);
            });

            // Register the Worker that runs the Initializer
            services.AddHostedService<DbMigrationWorker>();
        }
    }
}