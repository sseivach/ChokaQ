// ============================================================================
// PROJECT: ChokaQ
// DESCRIPTION: Universal worker that executes jobs from IJobStorage.
// ============================================================================

using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions;
using ChokaQ.Core.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Workers;

/// <summary>
/// The main background engine of ChokaQ. 
/// It fetches batches of jobs and delegates them to the JobProcessor.
/// </summary>
public class JobWorker : BackgroundService
{
    private readonly IJobStorage _storage;
    private readonly JobProcessor _processor;
    private readonly ILogger<JobWorker> _logger;

    // Unique identifier for this specific worker instance
    private readonly string _workerId = Guid.NewGuid().ToString("N");

    // TODO: Move to configuration options (WorkerOptions)
    private const int BatchSize = 10;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    public JobWorker(
        IJobStorage storage,
        JobProcessor processor,
        ILogger<JobWorker> logger)
    {
        _storage = storage;
        _processor = processor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChokaQ Worker {WorkerId} started.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. ATOMIC FETCH
                // Request a batch of pending jobs from the storage (SQL or Memory).
                // The storage marks them as 'Fetched' and assigns them to this WorkerId.
                var jobs = await _storage.FetchNextBatchAsync(_workerId, BatchSize, null, stoppingToken);

                if (!jobs.Any())
                {
                    // No jobs found, wait before next poll
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                _logger.LogDebug("Fetched {Count} jobs for processing.", jobs.Count());

                // 2. BATCH PROCESSING
                // We process jobs sequentially to ensure simple concurrency control.
                // For high-throughput scenarios, Task.WhenAll can be used with a Semaphore.
                foreach (var job in jobs)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await ProcessSingleJobAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break; // Graceful shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error in Worker loop. Restarting in 5s...");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("ChokaQ Worker {WorkerId} stopped.", _workerId);
    }

    private async Task ProcessSingleJobAsync(JobEntity job, CancellationToken ct)
    {
        try
        {
            // 3. EXECUTION & TRANSITION
            // Processor handles the Try/Catch logic and moves the job 
            // to Succeeded, Morgue, or schedules a Retry.
            await _processor.ProcessJobAsync(job, ct);
        }
        catch (Exception ex)
        {
            // This is a safety net for errors inside the Processor itself.
            _logger.LogError(ex, "Unexpected error in Processor for Job {JobId}", job.Id);
        }
    }
}