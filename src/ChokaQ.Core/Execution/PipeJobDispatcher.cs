using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Middleware;
using ChokaQ.Core.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace ChokaQ.Core.Execution;

/// <summary>
/// Implementation of IJobDispatcher for the "Pipe" strategy.
/// Delegates all jobs to a single registered IChokaQPipeHandler.
/// </summary>
public class PipeJobDispatcher : IJobDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PipeJobDispatcher> _logger;

    public PipeJobDispatcher(IServiceScopeFactory scopeFactory, ILogger<PipeJobDispatcher> logger)
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

        // 2. Resolve the single global handler
        var handler = serviceProvider.GetService<IChokaQPipeHandler>();

        if (handler == null)
        {
            throw new InvalidOperationException("Pipe mode is enabled, but no implementation of IChokaQPipeHandler was found in the DI container.");
        }

        // 3. Build Pipeline
        var middlewares = serviceProvider.GetServices<IChokaQMiddleware>().Reverse().ToList();

        JobDelegate pipeline = async () =>
        {
            await handler.HandleAsync(jobType, payload, ct);
        };

        // Wrap the core handler with middlewares
        foreach (var middleware in middlewares)
        {
            var next = pipeline;
            // In Pipe mode, we pass the raw payload string as the `job` object
            pipeline = () => middleware.InvokeAsync(jobContext, payload, next);
        }

        // 4. Execute Pipeline
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
}