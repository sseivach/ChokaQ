using ChokaQ.Abstractions;
using ChokaQ.Core.Contexts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace ChokaQ.Core.Execution;

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
        using var scope = _scopeFactory.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        // 1. Setup Context
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
            var task = (Task)method.Invoke(handler, new object[] { job, ct })!;
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