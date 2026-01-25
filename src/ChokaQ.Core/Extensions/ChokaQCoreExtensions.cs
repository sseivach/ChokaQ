using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Resilience;
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

        // Pass options to Infrastructure registration
        AddInfrastructure(services, options);

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
        services.TryAddSingleton<IDeduplicator, InMemoryDeduplicator>();
        services.TryAddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();

        // Register InMemoryJobStorage with the configured options
        services.TryAddSingleton<IJobStorage>(sp => new InMemoryJobStorage(options.InMemoryOptions));

        services.TryAddSingleton<IChokaQNotifier, NullNotifier>();
        services.TryAddSingleton<InMemoryQueue>();
        services.TryAddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

        services.TryAddScoped<JobContext>();
        services.TryAddScoped<IJobContext>(sp => sp.GetRequiredService<JobContext>());
        services.TryAddSingleton<IJobStateManager, JobStateManager>();

        services.TryAddSingleton<IJobProcessor, JobProcessor>();
        services.TryAddSingleton<JobWorker>();
        services.TryAddSingleton<IWorkerManager>(sp => sp.GetRequiredService<JobWorker>());

        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());
    }

    // ... (AddPipeStrategy and AddBusStrategy remain unchanged) ...
    private static void AddPipeStrategy(IServiceCollection services, ChokaQOptions options)
    {
        services.TryAddSingleton<IJobDispatcher, PipeJobDispatcher>();

        if (options.PipeHandlerType != null)
        {
            services.TryAddTransient(typeof(IChokaQPipeHandler), options.PipeHandlerType);
        }
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
                    registry.Register(reg.Key, reg.JobType);
                    var interfaceType = typeof(IChokaQJobHandler<>).MakeGenericType(reg.JobType);
                    services.TryAddTransient(interfaceType, reg.HandlerType);
                }
            }
        }
        services.AddSingleton(registry);
    }
}