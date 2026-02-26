using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Middleware;
using ChokaQ.Core.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChokaQ.Core.Execution;

public class BusJobDispatcher : IJobDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobTypeRegistry _registry;
    private readonly ILogger<BusJobDispatcher> _logger;

    // Cache for compiled delegates: Fast execution without Reflection overhead
    // Signature: Func<object handler, IChokaQJob job, CancellationToken ct, Task>
    private static readonly ConcurrentDictionary<Type, Func<object, IChokaQJob, CancellationToken, Task>> _handlerCache = new();

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

        // 2. Resolve Type from Registry
        var clrType = _registry.GetTypeByKey(jobType) ?? Type.GetType(jobType);

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

        // 5. Get or Build Compiled Delegate (Expression Trees Magic)
        var executeDelegate = GetOrCompileHandlerDelegate(handlerType, clrType);

        // 6. Build Pipeline (Middlewares + Core Handler)
        var middlewares = serviceProvider.GetServices<IChokaQMiddleware>().Reverse().ToList();

        JobDelegate pipeline = async () =>
        {
            try
            {
                // BOOM! Ultra-fast execution via compiled delegate
                await executeDelegate(handler, jobObject, ct);
            }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException != null) throw ex.InnerException;
                throw;
            }
        };

        // Wrap the core handler with middlewares
        foreach (var middleware in middlewares)
        {
            var next = pipeline;
            pipeline = () => middleware.InvokeAsync(jobContext, jobObject, next);
        }

        // 7. Execute Pipeline
        await pipeline();
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

    /// <summary>
    /// Builds a fast compiled delegate using Expression Trees to avoid Reflection Invoke overhead.
    /// </summary>
    private static Func<object, IChokaQJob, CancellationToken, Task> GetOrCompileHandlerDelegate(Type handlerType, Type jobType)
    {
        return _handlerCache.GetOrAdd(handlerType, type =>
        {
            // Parameters for the resulting delegate
            var handlerParam = Expression.Parameter(typeof(object), "handler");
            var jobParam = Expression.Parameter(typeof(IChokaQJob), "job");
            var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

            // Cast parameters to their actual types
            var castedHandler = Expression.Convert(handlerParam, handlerType);
            var castedJob = Expression.Convert(jobParam, jobType);

            // Find the HandleAsync method
            var methodInfo = handlerType.GetMethod("HandleAsync");
            if (methodInfo == null)
            {
                throw new InvalidOperationException($"Method 'HandleAsync' not found on {handlerType.Name}");
            }

            // Create the method call expression: ((THandler)handler).HandleAsync((TJob)job, ct)
            var callExpr = Expression.Call(castedHandler, methodInfo, castedJob, ctParam);

            // Compile to Func<object, IChokaQJob, CancellationToken, Task>
            var lambda = Expression.Lambda<Func<object, IChokaQJob, CancellationToken, Task>>(
                callExpr, handlerParam, jobParam, ctParam);

            return lambda.Compile();
        });
    }
}