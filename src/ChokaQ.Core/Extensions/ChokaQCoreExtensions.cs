using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Notifiers;
using ChokaQ.Core.Queues;
using ChokaQ.Core.Resilience;
using ChokaQ.Core.Storages;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.TryAddSingleton<IJobStorage, InMemoryJobStorage>();

        // 4. Notification (Default to Null/Silent)
        services.TryAddSingleton<IChokaQNotifier, NullNotifier>();

        // 5. Register JobContext as Scoped. 
        services.TryAddScoped<IJobContext, JobContext>();
        services.TryAddScoped<JobContext>(); // Allow resolution of concrete type in Executor

        // 6. Queue System
        services.TryAddSingleton<InMemoryQueue>();
        services.TryAddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

        // 7. Execution Engine
        // We decouple execution logic from the worker orchestration
        services.TryAddSingleton<IJobExecutor, JobExecutor>();

        // 8. The Engine (Background Worker)
        services.TryAddSingleton<JobWorker>();
        services.TryAddSingleton<IWorkerManager>(sp => sp.GetRequiredService<JobWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());

        return services;
    }
}