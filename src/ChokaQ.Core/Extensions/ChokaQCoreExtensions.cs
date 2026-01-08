using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Execution;
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
    public static IServiceCollection AddChokaQ(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();
        services.TryAddSingleton<IJobStorage, InMemoryJobStorage>();
        services.TryAddSingleton<IChokaQNotifier, NullNotifier>();
        services.TryAddScoped<IJobContext, JobContext>();
        services.TryAddScoped<JobContext>();
        services.TryAddSingleton<InMemoryQueue>();
        services.TryAddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

        // Execution Logic
        services.TryAddSingleton<IJobExecutor, JobExecutor>();

        // Orchestration (Worker)
        services.TryAddSingleton<JobWorker>();
        // IWorkerManager now lives in Abstractions, but implementation is still JobWorker
        services.TryAddSingleton<IWorkerManager>(sp => sp.GetRequiredService<JobWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());

        return services;
    }
}