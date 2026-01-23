using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // 3. Execute
        // We pass the raw type string and payload directly to the user code.
        await handler.HandleAsync(jobType, payload, ct);
    }
}