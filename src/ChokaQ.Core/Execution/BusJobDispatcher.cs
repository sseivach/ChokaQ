using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChokaQ.Core.Execution;

/// <summary>
/// Implementation of IJobDispatcher for the "Bus" strategy.
/// Performs Type discovery, JSON deserialization, and Generic Handler resolution.
/// </summary>
public class BusJobDispatcher : IJobDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BusJobDispatcher> _logger;

    public BusJobDispatcher(IServiceScopeFactory scopeFactory, ILogger<BusJobDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task DispatchAsync(string jobId, string jobType, string payload, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        // 1. Setup Context
        var jobContext = serviceProvider.GetRequiredService<JobContext>();
        jobContext.JobId = jobId;

        // 2. Resolve CLR Type from string
        // In Bus mode, the jobType MUST be a valid Type Name.
        var clrType = Type.GetType(jobType);
        if (clrType == null)
        {
            // Try to search in loaded assemblies if simple name is provided
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                clrType = asm.GetType(jobType);
                if (clrType != null) break;
            }
        }

        if (clrType == null)
        {
            throw new InvalidOperationException($"Bus Mode: Could not resolve C# type for '{jobType}'. Ensure the assembly is loaded.");
        }

        // 3. Deserialize Payload
        var jobObject = JsonSerializer.Deserialize(payload, clrType) as IChokaQJob;
        if (jobObject == null)
        {
            throw new InvalidOperationException($"Bus Mode: Failed to deserialize payload into type '{clrType.Name}'.");
        }

        // 4. Resolve Generic Handler (IChokaQJobHandler<T>)
        var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(clrType);
        var handler = serviceProvider.GetService(handlerType);

        if (handler == null)
        {
            throw new InvalidOperationException($"Bus Mode: No handler registered for job type '{clrType.Name}'.");
        }

        // 5. Invoke HandleAsync via Reflection
        var method = handlerType.GetMethod("HandleAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'HandleAsync' not found on handler {handlerType.Name}");
        }

        try
        {
            var task = (Task)method.Invoke(handler, new object[] { jobObject, ct })!;
            await task;
        }
        catch (TargetInvocationException ex)
        {
            if (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
            throw;
        }
    }
}