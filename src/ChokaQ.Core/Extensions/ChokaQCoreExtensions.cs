using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Notifiers;
using ChokaQ.Core.Queues;
using ChokaQ.Core.Resilience;
using ChokaQ.Core.Storages;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions; // Need this for TryAdd

namespace ChokaQ.Core.Extensions;

public static class ChokaQCoreExtensions
{
    /// <summary>
    /// Adds ChokaQ core services to the application container.
    /// </summary>
    public static IServiceCollection AddChokaQ(this IServiceCollection services)
    {
        // 1. Core Utilities
        services.TryAddSingleton(TimeProvider.System);

        // 2. Register Circuit Breaker
        services.TryAddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();

        // 3. Storage (Default to InMemory)
        // TryAddSingleton allows the user to register their own IJobStorage BEFORE calling AddChokaQ
        // and we won't overwrite it (e.g., if they want SQL Server).
        services.TryAddSingleton<IJobStorage, InMemoryJobStorage>();

        // 4. Notification (Default to Null/Silent)
        // The user (SampleApp) will override this with SignalRNotifier later if they have a UI.
        services.TryAddSingleton<IChokaQNotifier, NullNotifier>();

        // 5. Register JobContext as Scoped. 
        // A new instance is created for each scope (each job execution).
        services.TryAddScoped<IJobContext, JobContext>();
        services.TryAddScoped<JobContext>();

        // 6. Queue System
        // We register the concrete class first
        services.TryAddSingleton<InMemoryQueue>();
        // Then we alias the interface to use the same instance
        services.TryAddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

        // 7. The Engine (Background Worker)
        services.TryAddSingleton<JobWorker>();
        services.TryAddSingleton<IWorkerManager>(sp => sp.GetRequiredService<JobWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());

        return services;
    }
}