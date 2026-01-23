using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChokaQ.Core.Extensions;

public static class ChokaQCoreExtensions
{
    public static IServiceCollection AddChokaQ(this IServiceCollection services, Action<ChokaQOptions>? configure = null)
    {
        var options = new ChokaQOptions();
        configure?.Invoke(options);

        // 1. Register Common Infrastructure (Time, Storage, State, etc.)
        AddInfrastructure(services);

        // 2. Register Processing Strategy (Bus vs Pipe)
        if (options.IsPipeMode)
        {
            AddPipeStrategy(services, options);
        }
        else
        {
            AddBusStrategy(services, options);
        }

        return services;
    }

    private static void AddInfrastructure(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        // Defaults (Can be replaced by SQL/Redis providers)
        services.TryAddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();
        services.TryAddSingleton<IJobStorage, InMemoryJobStorage>();
        services.TryAddSingleton<IChokaQNotifier, NullNotifier>();
        services.TryAddSingleton<InMemoryQueue>();
        services.TryAddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

        // Context & State
        services.TryAddScoped<JobContext>();
        services.TryAddScoped<IJobContext>(sp => sp.GetRequiredService<JobContext>());
        services.TryAddSingleton<IJobStateManager, JobStateManager>();

        // Worker & Processing
        services.TryAddSingleton<IJobProcessor, JobProcessor>();
        services.TryAddSingleton<JobWorker>(); // The In-Memory Worker
        services.TryAddSingleton<IWorkerManager>(sp => sp.GetRequiredService<JobWorker>());

        // Register the Worker as Hosted Service (Run in background)
        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());
    }

    private static void AddPipeStrategy(IServiceCollection services, ChokaQOptions options)
    {
        services.TryAddSingleton<IJobDispatcher, PipeJobDispatcher>();

        if (options.PipeHandlerType != null)
        {
            services.TryAddTransient(typeof(IChokaQPipeHandler), options.PipeHandlerType);
        }

        // Empty registry needed for dependencies
        services.TryAddSingleton(new JobTypeRegistry());
    }

    private static void AddBusStrategy(IServiceCollection services, ChokaQOptions options)
    {
        services.TryAddSingleton<IJobDispatcher, BusJobDispatcher>();

        var registry = new JobTypeRegistry();

        foreach (var profileType in options.ProfileTypes)
        {
            if (Activator.CreateInstance(profileType) is ChokaQJobProfile profileInstance)
            {
                foreach (var reg in profileInstance.Registrations)
                {
                    // A. Populate Registry
                    registry.Register(reg.Key, reg.JobType);

                    // B. Register Handler in DI
                    var interfaceType = typeof(IChokaQJobHandler<>).MakeGenericType(reg.JobType);
                    services.TryAddTransient(interfaceType, reg.HandlerType);
                }
            }
        }

        services.AddSingleton(registry);
    }
}