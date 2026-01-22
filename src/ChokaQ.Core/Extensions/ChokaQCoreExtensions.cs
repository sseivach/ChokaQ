using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Handlers;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Reflection;

namespace ChokaQ.Core.Extensions;

public static class ChokaQCoreExtensions
{
    /// <summary>
    /// Registers ChokaQ Core services.
    /// </summary>
    /// <param name="configure">Configuration action to select Pipe or Bus mode.</param>
    public static IServiceCollection AddChokaQ(this IServiceCollection services, Action<ChokaQOptions>? configure = null)
    {
        // 1. Run Configuration
        var options = new ChokaQOptions();

        // If no config provided, default to Bus mode with calling assembly
        if (configure == null)
        {
            options.UseBus(Assembly.GetCallingAssembly().GetTypes()[0]);
        }
        else
        {
            configure(options);
        }

        // 2. Register Dispatcher Strategy
        if (options.IsPipeMode)
        {
            // Strategy A: Pipe
            services.TryAddSingleton<IJobDispatcher, PipeJobDispatcher>();

            // Register the user's handler
            if (options.PipeHandlerType != null)
            {
                services.TryAddTransient(typeof(IChokaQPipeHandler), options.PipeHandlerType);
            }
        }
        else
        {
            // Strategy B: Bus
            services.TryAddSingleton<IJobDispatcher, BusJobDispatcher>();

            // Register System Test Handler by default for Bus mode
            services.TryAddTransient<IChokaQJobHandler<SystemTestJob>, SystemTestJobHandler>();

            // Future: Here we would implement Assembly Scanning for other handlers based on options.ScanAssemblies
        }

        // 3. Common Services
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();
        services.TryAddSingleton<IJobStorage, InMemoryJobStorage>();
        services.TryAddSingleton<IChokaQNotifier, NullNotifier>();

        services.TryAddScoped<JobContext>();
        services.TryAddScoped<IJobContext>(sp => sp.GetRequiredService<JobContext>());

        services.TryAddSingleton<InMemoryQueue>();
        services.TryAddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

        services.TryAddSingleton<IJobStateManager, JobStateManager>();

        // JobProcessor now depends on IJobDispatcher
        services.TryAddSingleton<IJobProcessor, JobProcessor>();

        services.TryAddSingleton<JobWorker>();
        services.TryAddSingleton<IWorkerManager>(sp => sp.GetRequiredService<JobWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());

        return services;
    }
}