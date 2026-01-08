using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace ChokaQ.Core.Workers;

public class JobExecutor : IJobExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobExecutor> _logger;

    public JobExecutor(IServiceScopeFactory scopeFactory, ILogger<JobExecutor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ExecuteJobAsync(IChokaQJob job, CancellationToken ct)
    {
        // Create a clean scope for this specific job execution.
        // This ensures that Scoped services (like EF Core DbContext) are fresh.
        using var scope = _scopeFactory.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        // 1. Setup Context
        // This allows the handler to access metadata (like JobId) via DI.
        var jobContext = serviceProvider.GetRequiredService<JobContext>();
        jobContext.JobId = job.Id;

        // 2. Resolve Handler
        var jobType = job.GetType();
        var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(jobType);
        var handler = serviceProvider.GetService(handlerType);

        if (handler == null)
        {
            throw new InvalidOperationException($"No handler registered for job type: {jobType.Name}");
        }

        // 3. Invoke HandleAsync via Reflection
        var method = handlerType.GetMethod("HandleAsync");
        if (method == null)
        {
            throw new InvalidOperationException($"Method 'HandleAsync' not found on handler {handlerType.Name}");
        }

        try
        {
            // Invoke the handler.
            // Note: If the handler throws, the exception bubbles up to the Worker (as intended).
            var task = (Task)method.Invoke(handler, new object[] { job, ct })!;
            await task;
        }
        catch (TargetInvocationException ex)
        {
            // Reflection wraps exceptions in TargetInvocationException.
            // We unwrap it to preserve the original stack trace and exception type
            // so the Worker can handle OperationCanceledException correctly.
            if (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
            throw;
        }
    }
}