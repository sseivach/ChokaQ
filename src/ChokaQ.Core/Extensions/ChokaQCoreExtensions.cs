using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Resilience;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Processing;
using ChokaQ.Core.Storage;
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

        // 1. Core Services & Infrastructure
        AddInfrastructure(services, options);

        // 2. Execution Strategy (Pipe vs Bus)
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

    private static void AddInfrastructure(IServiceCollection services, ChokaQOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);

        // Deduplication
        services.TryAddSingleton<IDeduplicator, InMemoryDeduplicator>();

        // Default Storage (InMemory)

        services.TryAddSingleton<IJobStorage>(sp => new InMemoryJobStorage());

        // Job Context
        services.TryAddScoped<JobContext>();

        // THE BRAIN: Job Processor
        services.TryAddSingleton<JobProcessor>();

        // THE ENGINE: Universal Worker
        services.AddHostedService<JobWorker>();
    }

    private static void AddPipeStrategy(IServiceCollection services, ChokaQOptions options)
    {
        // Strategy: Delegate everything to a single Global Handler
        services.TryAddSingleton<IJobDispatcher, PipeJobDispatcher>();

        if (options.PipeHandlerType != null)
        {
            services.TryAddTransient(typeof(IChokaQPipeHandler), options.PipeHandlerType);
        }

        // Empty registry for Pipe mode (or basic one)
        services.TryAddSingleton(new JobTypeRegistry());
    }

    private static void AddBusStrategy(IServiceCollection services, ChokaQOptions options)
    {
        // Strategy: Route based on Type using Registry
        services.TryAddSingleton<IJobDispatcher, BusJobDispatcher>();

        var registry = new JobTypeRegistry();

        // Scan profiles to register handlers
        foreach (var profileType in options.ProfileTypes)
        {
            if (Activator.CreateInstance(profileType) is ChokaQJobProfile profileInstance)
            {
                foreach (var reg in profileInstance.Registrations)
                {
                    registry.Register(reg.Key, reg.JobType);

                    // Register Handler: IChokaQJobHandler<TJob>
                    var interfaceType = typeof(IChokaQJobHandler<>).MakeGenericType(reg.JobType);
                    services.TryAddTransient(interfaceType, reg.HandlerType);
                }
            }
        }
        services.AddSingleton(registry);
    }
}