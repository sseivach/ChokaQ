using System.Reflection;
using System.Text.Json;
using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Core.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Execution;

public class BusJobDispatcher : IJobDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobTypeRegistry _registry;
    private readonly ILogger<BusJobDispatcher> _logger;

    public BusJobDispatcher(
        IServiceScopeFactory scopeFactory,
        JobTypeRegistry registry,
        ILogger<BusJobDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _logger = logger;
    }

    public async Task ExecuteAsync(JobEntity job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        // 1. Setup Context (JobId allows scoped services to know "who" calls them)
        var jobContext = serviceProvider.GetRequiredService<JobContext>();
        jobContext.JobId = job.Id;

        // 2. Resolve Type
        var clrType = _registry.GetTypeByKey(job.Type) ?? Type.GetType(job.Type);

        if (clrType == null)
        {
            throw new InvalidOperationException($"Bus Mode: Unknown Job Type '{job.Type}'. Ensure the assembly is scanned.");
        }

        // 3. Deserialize Payload
        // Note: We handle null payload as "default" for parameterless jobs if needed, 
        // but typically a payload is expected.
        var jobObject = string.IsNullOrEmpty(job.Payload)
            ? null
            : JsonSerializer.Deserialize(job.Payload, clrType) as IChokaQJob;

        if (jobObject == null && !string.IsNullOrEmpty(job.Payload))
        {
            throw new InvalidOperationException($"Bus Mode: Failed to deserialize payload for '{clrType.Name}'.");
        }

        // 4. Resolve Handler (Generic: IChokaQJobHandler<T>)
        var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(clrType);
        var handler = serviceProvider.GetService(handlerType);

        if (handler == null)
        {
            throw new InvalidOperationException($"Bus Mode: No IChokaQJobHandler<{clrType.Name}> found in DI.");
        }

        // 5. Invoke HandleAsync
        var method = handlerType.GetMethod("HandleAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'HandleAsync' not found on {handlerType.Name}");
        }

        try
        {
            // Invoke: Task HandleAsync(TJob job, CancellationToken ct)
            var task = (Task)method.Invoke(handler, new object[] { jobObject!, ct })!;
            await task;
        }
        catch (TargetInvocationException ex)
        {
            // Unwrap reflection exception to get the real error
            if (ex.InnerException != null) throw ex.InnerException;
            throw;
        }
    }
}