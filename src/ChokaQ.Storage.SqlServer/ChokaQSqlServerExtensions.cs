using ChokaQ.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChokaQ.Storage.SqlServer;

public static class ChokaQSqlServerExtensions
{
    public static void UseSqlServer(this IServiceCollection services, Action<SqlJobStorageOptions> configure)
    {
        var options = new SqlJobStorageOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentNullException(nameof(options.ConnectionString));

        services.RemoveAll<IJobStorage>();
        services.Configure<SqlJobStorageOptions>(opt =>
        {
            opt.ConnectionString = options.ConnectionString;
            opt.SchemaName = options.SchemaName;
            opt.AutoCreateSqlTable = options.AutoCreateSqlTable;
        });

        services.AddSingleton<IJobStorage, SqlJobStorage>();

        if (options.AutoCreateSqlTable)
        {
            services.AddTransient<SqlInitializer>();
            services.AddHostedService<DbMigrationWorker>();
        }
    }
}