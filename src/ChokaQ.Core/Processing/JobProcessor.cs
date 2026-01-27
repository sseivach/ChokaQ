// ============================================================================
// PROJECT: ChokaQ
// DESCRIPTION: Core execution engine implementing Three Pillars logic.
// ============================================================================

using System.Diagnostics;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Execution;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Processing;

public class JobProcessor
{
    private readonly IJobStorage _storage;
    private readonly IJobDispatcher _dispatcher;
    private readonly ILogger<JobProcessor> _logger;

    // TODO: Move to configuration options
    private const int MaxRetries = 5;

    public JobProcessor(
        IJobStorage storage,
        IJobDispatcher dispatcher,
        ILogger<JobProcessor> logger)
    {
        _storage = storage;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Executes a job and handles its lifecycle transitions (Hot -> Archive/Morgue/Retry).
    /// </summary>
    public async Task ProcessJobAsync(JobEntity job, CancellationToken ct)
    {
        // Start measuring execution time for metrics
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. EXECUTION
            // Delegate execution to the Dispatcher
            await _dispatcher.ExecuteAsync(job, ct);

            sw.Stop();

            // 2. SUCCESS TRANSITION (Move to Archive)
            var successRecord = new JobSucceededEntity(
                Id: job.Id,
                Queue: job.Queue,
                Type: job.Type,
                Payload: job.Payload,
                Tags: job.Tags,
                WorkerId: job.WorkerId,
                CreatedBy: job.CreatedBy,
                LastModifiedBy: "System",
                FinishedAtUtc: DateTime.UtcNow,
                DurationMs: sw.Elapsed.TotalMilliseconds // We store duration only for success
            );

            // Atomically move from Hot to Succeeded
            await _storage.ArchiveAsSuccessAsync(successRecord, ct);

            _logger.LogInformation("Job {JobId} succeeded in {Duration}ms", job.Id, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Note: We don't store duration in Morgue because JobMorgueEntity lacks a 'DurationMs' field.

            // 3. FAILURE HANDLING
            if (job.AttemptCount < MaxRetries)
            {
                // A. RETRY (Stay in Hot)
                var nextAttempt = job.AttemptCount + 1;
                var delay = CalculateBackoff(nextAttempt);

                _logger.LogWarning(ex, "Job {JobId} failed (Attempt {Attempt}/{Max}). Retrying in {Delay}s...",
                    job.Id, nextAttempt, MaxRetries, delay.TotalSeconds);

                // Update job state in Hot storage
                await _storage.RetryJobAsync(job.Id, nextAttempt, delay, ex.Message, ct);
            }
            else
            {
                // B. MORGUE (Move to Dead Letter Queue)
                _logger.LogError(ex, "Job {JobId} failed permanently after {Attempt} attempts. Moving to Morgue.", job.AttemptCount);

                var corpse = new JobMorgueEntity(
                    Id: job.Id,
                    Queue: job.Queue,
                    Type: job.Type,
                    Payload: job.Payload,
                    Tags: job.Tags,
                    ErrorDetails: ex.ToString(),
                    AttemptCount: job.AttemptCount,
                    WorkerId: job.WorkerId,
                    CreatedBy: job.CreatedBy,
                    LastModifiedBy: "System",
                    FailedAtUtc: DateTime.UtcNow
                );

                // Atomically move from Hot to Morgue
                await _storage.ArchiveAsMorgueAsync(corpse, ct);
            }
        }
    }

    /// <summary>
    /// Exponential Backoff strategy: 2s, 4s, 8s, 16s, 32s...
    /// </summary>
    private static TimeSpan CalculateBackoff(int attempt)
    {
        var seconds = Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 3600)); // Max 1 hour
    }
}