using ChokaQ.Abstractions;
using ChokaQ.Core.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Workers;

public class JobWorker : BackgroundService
{
    private readonly ILogger<JobWorker> _logger;
    private readonly InMemoryQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;

    public JobWorker(
        InMemoryQueue queue,
        ILogger<JobWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _queue = queue;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ChokaQ] Worker started. Waiting for jobs...");

        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            while (_queue.Reader.TryRead(out var job))
            {
                try
                {
                    await ProcessJobAsync(job, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[ChokaQ] Failed to process job {job.GetType().Name}");
                }
            }
        }
    }

    private async Task ProcessJobAsync(IChokaQJob job, CancellationToken ct)
    {
        // 1. Create a scope (because Handlers might use DbContext, which is Scoped)
        using var scope = _scopeFactory.CreateScope();

        // 2. Determine the job type (e.g., "SendEmailJob")
        var jobType = job.GetType();

        // 3. Determine the handler type (e.g., "IChokaQJobHandler<SendEmailJob>")
        var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(jobType);

        // 4. Resolve the handler from DI
        var handler = scope.ServiceProvider.GetService(handlerType);

        if (handler == null)
        {
            _logger.LogError($"[ChokaQ] No handler registered for job type: {jobType.Name}");
            return;
        }

        // 5. Invoke HandleAsync via Reflection
        // Since we don't know TJob at compile time here, we must use reflection or 'dynamic'.
        // 'dynamic' is cleaner to read but slightly slower. Reflection is safer. 
        // Let's use standard Reflection for method invocation.
        var method = handlerType.GetMethod("HandleAsync");

        if (method != null)
        {
            await (Task)method.Invoke(handler, new object[] { job, ct })!;
        }
    }
}