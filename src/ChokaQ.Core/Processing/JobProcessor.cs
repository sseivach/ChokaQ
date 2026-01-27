using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Execution;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace ChokaQ.Core.Processing;

/// <summary>
/// The core execution engine responsible for running jobs and handling lifecycle transitions.
/// It acts as the orchestrator between Storage, Dispatcher, and UI Notifier.
/// </summary>
public class JobProcessor
{
    private readonly IJobStorage _storage;
    private readonly IJobDispatcher _dispatcher;
    private readonly IChokaQNotifier _notifier;
    private readonly ILogger<JobProcessor> _logger;

    // TODO: Should be moved to configuration options
    private const int MaxRetries = 5;

    public JobProcessor(
        IJobStorage storage,
        IJobDispatcher dispatcher,
        IChokaQNotifier notifier,
        ILogger<JobProcessor> logger)
    {
        _storage = storage;
        _dispatcher = dispatcher;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// Executes a job and handles its atomic transition to Success, Retry, or Morgue.
    /// </summary>
    public async Task ProcessJobAsync(JobEntity job, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. Notify UI: Processing Started
            // We map internal DB status to UI Status "Processing"
            NotifyUi(job, JobUIStatus.Processing);

            // 2. EXECUTION (The Business Logic)
            await _dispatcher.ExecuteAsync(job, ct);

            sw.Stop();
            var duration = sw.Elapsed.TotalMilliseconds;

            // 3. SUCCESS TRANSITION (Atomic Move to Archive)
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
                DurationMs: duration
            );

            await _storage.ArchiveAsSuccessAsync(successRecord, ct);

            // Notify UI: Succeeded (Green state)
            NotifyUi(job, JobUIStatus.Succeeded, duration);

            _logger.LogInformation("Job {JobId} succeeded in {Duration}ms", job.Id, duration);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var duration = sw.Elapsed.TotalMilliseconds;

            // 4. FAILURE HANDLING
            if (job.AttemptCount < MaxRetries)
            {
                // A. RETRY (Stay in Hot Storage)
                // We calculate exponential backoff and update the record in the DB.
                var nextAttempt = job.AttemptCount + 1;
                var delay = CalculateBackoff(nextAttempt);

                _logger.LogWarning(ex, "Job {JobId} failed (Attempt {Attempt}). Retrying in {Delay}s...",
                    job.Id, nextAttempt, delay.TotalSeconds);

                await _storage.RetryJobAsync(job.Id, nextAttempt, delay, ex.Message, ct);

                // Notify UI: Retrying (Yellow state)
                NotifyUi(job, JobUIStatus.Retrying, duration);
            }
            else
            {
                // B. MORGUE (Move to Dead Letter Queue)
                // Retries exhausted. The job is moved to the Morgue table for manual inspection.
                _logger.LogError(ex, "Job {JobId} failed permanently. Moving to Morgue.", job.Id);

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

                await _storage.ArchiveAsMorgueAsync(corpse, ct);

                // Notify UI: Morgue (Red state)
                NotifyUi(job, JobUIStatus.Morgue, duration);
            }
        }
    }

    /// <summary>
    /// Helper to send fire-and-forget UI notifications.
    /// Ensures that SignalR failures do not block or crash the job processing pipeline.
    /// </summary>
    private void NotifyUi(JobEntity job, JobUIStatus status, double? duration = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _notifier.NotifyJobUpdatedAsync(
                    job.Id,
                    job.Type,
                    status,
                    job.AttemptCount,
                    duration,
                    job.CreatedBy,
                    // Only send StartedAt if we are just starting processing
                    status == JobUIStatus.Processing ? DateTime.UtcNow : null,
                    job.Queue,
                    job.Priority
                );
            }
            catch (Exception ex)
            {
                // Log but do not throw. UI updates are non-critical.
                _logger.LogWarning("Failed to push UI notification for Job {JobId}: {Message}", job.Id, ex.Message);
            }
        });
    }

    /// <summary>
    /// Calculates exponential backoff delay: 2^attempt seconds.
    /// Clamped between 2s and 1 hour.
    /// </summary>
    private static TimeSpan CalculateBackoff(int attempt)
    {
        var seconds = Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 3600));
    }
}