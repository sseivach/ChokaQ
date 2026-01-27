using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Resilience;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Processing;
using ChokaQ.Core.Resilience;
using ChokaQ.Core.State;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Extensions;

/// <summary>
/// Extension methods for registering ChokaQ services in the DI container.
/// </summary>
public static class ChokaQCoreExtensions
{
    /// <summary>
    /// Registers ChokaQ background job processing services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback for ChokaQOptions.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Basic usage (Bus Mode with profiles):
    /// <code>
    /// services.AddChokaQ(options =>
    /// {
    ///     options.AddProfile&lt;MailingProfile&gt;();
    ///     options.AddProfile&lt;ReportingProfile&gt;();
    /// });
    /// </code>
    /// 
    /// Pipe Mode (high-throughput):
    /// <code>
    /// services.AddChokaQ(options =>
    /// {
    ///     options.UsePipe&lt;GlobalPipeHandler&gt;();
    ///     options.ConfigureInMemory(o => o.MaxCapacity = 100_000);
    /// });
    /// </code>
    /// 
    /// For SQL Server persistence, chain with AddChokaQSqlServer():
    /// <code>
    /// services.AddChokaQ(options => options.AddProfile&lt;MyProfile&gt;())
    ///         .AddChokaQSqlServer(connectionString);
    /// </code>
    /// </remarks>
    public static IServiceCollection AddChokaQ(this IServiceCollection services, Action<ChokaQOptions>? configure = null)
    {
        var options = new ChokaQOptions();
        configure?.Invoke(options);

        // Register core infrastructure (storage, queues, processors)
        AddInfrastructure(services, options);

        // Register strategy-specific services (Bus vs Pipe)
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

    /// <summary>
    /// Registers core infrastructure services shared by both Bus and Pipe modes.
    /// Uses TryAdd to allow external packages (e.g., SqlServer) to override defaults.
    /// </summary>
    private static void AddInfrastructure(IServiceCollection services, ChokaQOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDeduplicator, InMemoryDeduplicator>();
        services.TryAddSingleton<ICircuitBreaker, InMemoryCircuitBreaker>();

        // Register InMemoryJobStorage with the configured options (Three Pillars)
        services.TryAddSingleton<IJobStorage>(sp => new InMemoryJobStorage(options.InMemoryOptions));
        services.TryAddSingleton<IChokaQNotifier, NullNotifier>();
        services.TryAddSingleton<InMemoryQueue>();
        services.TryAddSingleton<IChokaQQueue>(sp => sp.GetRequiredService<InMemoryQueue>());

        services.TryAddScoped<JobContext>();
        services.TryAddScoped<IJobContext>(sp => sp.GetRequiredService<JobContext>());
        services.TryAddSingleton<IJobStateManager, JobStateManager>();
        services.TryAddSingleton<IJobProcessor>(sp => new JobProcessor(
            sp.GetRequiredService<IJobStorage>(),
            sp.GetRequiredService<ILogger<JobProcessor>>(),
            sp.GetRequiredService<ICircuitBreaker>(),
            sp.GetRequiredService<IJobDispatcher>(),
            sp.GetRequiredService<IJobStateManager>(),
            options
        ));
        services.TryAddSingleton<JobWorker>();
        services.TryAddSingleton<IWorkerManager>(sp => sp.GetRequiredService<JobWorker>());

        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());

        // Register the Zombie Rescue Service
        services.AddHostedService<ZombieRescueService>();
    }

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
