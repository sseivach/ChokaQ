using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace ChokaQ.Core.Extensions;

public static class ChokaQCoreExtensions
{
    public static IServiceCollection AddChokaQ(this IServiceCollection services, Action<ChokaQOptions>? configure = null)
    {
        var options = new ChokaQOptions();

        if (configure != null)
        {
            configure(options);
        }

        // 1. Core Services (Infrastructure)
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();
        services.TryAddSingleton<IJobStorage, InMemoryJobStorage>();
        services.TryAddSingleton<IChokaQNotifier, NullNotifier>();
        services.TryAddScoped<JobContext>();
        services.TryAddScoped<IJobContext>(sp => sp.GetRequiredService<JobContext>());

        // Queue needs to know about Registry now
        services.TryAddSingleton<InMemoryQueue>();
        services.TryAddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

        services.TryAddSingleton<IJobStateManager, JobStateManager>();
        services.TryAddSingleton<IJobProcessor, JobProcessor>();
        services.TryAddSingleton<JobWorker>();
        services.TryAddSingleton<IWorkerManager>(sp => sp.GetRequiredService<JobWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());

        // 2. Dispatcher Strategy & Profile Processing
        if (options.IsPipeMode)
        {
            // --- PIPE MODE ---
            services.TryAddSingleton<IJobDispatcher, PipeJobDispatcher>();
            if (options.PipeHandlerType != null)
            {
                services.TryAddTransient(typeof(IChokaQPipeHandler), options.PipeHandlerType);
            }
            // Register empty registry for dependencies
            services.TryAddSingleton(new JobTypeRegistry());
        }
        else
        {
            // --- BUS MODE (Explicit Profiles) ---
            services.TryAddSingleton<IJobDispatcher, BusJobDispatcher>();

            var registry = new JobTypeRegistry();

            // Process each registered profile
            foreach (var profileType in options.ProfileTypes)
            {
                // Instantiate the profile to run its constructor
                if (Activator.CreateInstance(profileType) is ChokaQJobProfile profileInstance)
                {
                    foreach (var reg in profileInstance.Registrations)
                    {
                        // A. Populate Registry (Key <-> DTO Type)
                        registry.Register(reg.Key, reg.JobType);

                        // B. Register Handler in DI
                        // We register it as the Interface (IChokaQJobHandler<T>) 
                        // so BusJobDispatcher can resolve it.
                        var interfaceType = typeof(IChokaQJobHandler<>).MakeGenericType(reg.JobType);
                        services.TryAddTransient(interfaceType, reg.HandlerType);
                    }
                }
            }

            services.AddSingleton(registry);
        }

        return services;
    }
}