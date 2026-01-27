using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Core.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Execution;

/// <summary>
/// Implementation for "Pipe" strategy.
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

    public async Task ExecuteAsync(JobEntity job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        // 1. Setup Context
        var jobContext = serviceProvider.GetRequiredService<JobContext>();
        jobContext.JobId = job.Id;

        // 2. Resolve Global Handler
        var handler = serviceProvider.GetService<IChokaQPipeHandler>();

        if (handler == null)
        {
            throw new InvalidOperationException("Pipe mode is enabled, but no IChokaQPipeHandler found in DI.");
        }

        // 3. Execute
        // Pass raw type string and payload directly to user code.
        await handler.HandleAsync(job.Type, job.Payload ?? string.Empty, ct);
    }
}