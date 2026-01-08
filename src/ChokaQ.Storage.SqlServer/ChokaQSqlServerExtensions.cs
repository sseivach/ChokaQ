using ChokaQ.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Storage.SqlServer;

public static class ChokaQSqlServerExtensions
{
    /// <summary>
    /// Configures ChokaQ to use SQL Server as the storage provider.
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

        // 1. Register the Storage Implementation (Singleton)
        services.AddSingleton<IJobStorage>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SqlJobStorage>>();
            return new SqlJobStorage(options.ConnectionString, options.SchemaName, logger);
        });

        // 2. Register Auto-Provisioning logic (if enabled)
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