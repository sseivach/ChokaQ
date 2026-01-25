using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    public async Task DispatchAsync(string jobId, string jobType, string payload, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        // 1. Setup Context
        var jobContext = serviceProvider.GetRequiredService<JobContext>();
        jobContext.JobId = jobId;

        // 2. Resolve Type from Registry (Fast & Safe)
        var clrType = _registry.GetTypeByKey(jobType);

        // Fallback: Try standard Type.GetType if not in registry (legacy support)
        if (clrType == null)
        {
            clrType = Type.GetType(jobType);
        }

        if (clrType == null)
        {
            throw new InvalidOperationException($"Bus Mode: Unknown Job Type '{jobType}'. Ensure the job class is defined in a scanned assembly.");
        }

        // 3. Deserialize
        var jobObject = JsonSerializer.Deserialize(payload, clrType) as IChokaQJob;
        if (jobObject == null)
        {
            throw new InvalidOperationException($"Bus Mode: Failed to deserialize payload for '{clrType.Name}'.");
        }

        // 4. Resolve Handler
        var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(clrType);
        var handler = serviceProvider.GetService(handlerType);

        if (handler == null)
        {
            throw new InvalidOperationException($"Bus Mode: No IChokaQJobHandler<{clrType.Name}> found in DI. Did you forget to register it?");
        }

        // 5. Invoke
        var method = handlerType.GetMethod("HandleAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'HandleAsync' not found on {handlerType.Name}");
        }

        try
        {
            var task = (Task)method.Invoke(handler, new object[] { jobObject, ct })!;
            await task;
        }
        catch (TargetInvocationException ex)
        {
            if (ex.InnerException != null) throw ex.InnerException;
            throw;
        }
    }

    public JobMetadata ParseMetadata(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new JobMetadata("default", 10);

        try
        {
            var node = JsonNode.Parse(payload);
            var metaNode = node?["Metadata"];
            var queue = metaNode?["Queue"]?.ToString() ?? "default";
            var priority = metaNode?["Priority"]?.GetValue<int>() ?? 10;

            return new JobMetadata(queue, priority);
        }
        catch
        {
            return new JobMetadata("default", 10);
        }
    }
}