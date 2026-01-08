using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Background worker that triggers database initialization at application startup.
/// </summary>
public class DbMigrationWorker : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DbMigrationWorker> _logger;

    public DbMigrationWorker(IServiceProvider serviceProvider, ILogger<DbMigrationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Create a scope to resolve scoped/transient services
        using var scope = _serviceProvider.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<SqlInitializer>();

        _logger.LogInformation("Running automatic database migration...");
        await initializer.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}