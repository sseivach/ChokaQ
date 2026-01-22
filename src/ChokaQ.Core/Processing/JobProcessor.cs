using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Execution;
using ChokaQ.Core.State;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ChokaQ.Core.Processing;

public class JobProcessor : IJobProcessor
{
    private readonly IJobStorage _storage;
    private readonly ILogger<JobProcessor> _logger;
    private readonly ICircuitBreaker _breaker;
    private readonly IJobExecutor _executor;
    private readonly IJobStateManager _stateManager;
    private readonly ChokaQ.Core.Defaults.InMemoryQueue _queue; // Using concrete type for internal requeue logic

    // Registry of tokens for currently running jobs to allow cancellation
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 3;

    public JobProcessor(
        IJobStorage storage,
        ILogger<JobProcessor> logger,
        ICircuitBreaker breaker,
        IJobExecutor executor,
        IJobStateManager stateManager,
        ChokaQ.Core.Defaults.InMemoryQueue queue)
    {
        _storage = storage;
        _logger = logger;
        _breaker = breaker;
        _executor = executor;
        _stateManager = stateManager;
        _queue = queue;
    }

    // Exposed to allow external cancellation (e.g. from WorkerManager)
    public void CancelJob(string jobId)
    {
        if (_activeJobTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogInformation("Requesting cancellation for running job {JobId}...", jobId);
            cts.Cancel();
        }
    }

    public async Task ProcessJobAsync(IChokaQJob job, string workerId, CancellationToken workerCt)
    {
        var storageDto = await _storage.GetJobAsync(job.Id);

        // Check if already cancelled while in queue
        if (storageDto?.Status == JobStatus.Cancelled)
        {
            _logger.LogInformation("[Worker {ID}] Skipping job {JobId} because it was cancelled.", workerId, job.Id);
            return;
        }

        // Capture metadata including Queue and Priority to pass along with status updates
        var createdBy = storageDto?.CreatedBy;
        var queueName = storageDto?.Queue ?? "default"; // <--- Capture Queue
        var priority = storageDto?.Priority ?? 10;      // <--- Capture Priority
        int currentAttempt = storageDto?.AttemptCount ?? 1;
        var jobTypeName = job.GetType().Name;

        // [STEP 1] Check Circuit Breaker
        if (!_breaker.IsExecutionPermitted(jobTypeName))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", job.Id, jobTypeName);
            await Task.Delay(5000, workerCt);
            await _queue.RequeueAsync(job, workerCt);
            return;
        }

        // Set status to Processing
        // Capture Start Time
        var startedAt = DateTime.UtcNow;

        await _stateManager.UpdateStateAsync(
            job.Id,
            jobTypeName,
            JobStatus.Processing,
            currentAttempt,
            executionDurationMs: null,
            createdBy: createdBy,
            startedAtUtc: startedAt,
            queue: queueName,
            priority: priority,
            ct: workerCt);

        // Create a specific cancellation token for this job execution
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        _activeJobTokens.TryAdd(job.Id, jobCts);

        // Start Timer
        var sw = Stopwatch.StartNew();

        try
        {
            // Delegate execution
            await _executor.ExecuteJobAsync(job, jobCts.Token);

            // Stop Timer
            sw.Stop();
            var durationMs = sw.Elapsed.TotalMilliseconds;

            // Report Success
            _breaker.ReportSuccess(jobTypeName);

            await _stateManager.UpdateStateAsync(
                job.Id,
                jobTypeName,
                JobStatus.Succeeded,
                currentAttempt,
                executionDurationMs: durationMs,
                createdBy: createdBy,
                startedAtUtc: startedAt,
                queue: queueName,
                priority: priority,
                ct: workerCt);

            _logger.LogInformation("[Worker {ID}] Job {JobId} done in {Duration}ms.", workerId, job.Id, durationMs);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var durationMs = sw.Elapsed.TotalMilliseconds;

            _logger.LogWarning("[Worker {ID}] Job {JobId} was CANCELLED after {Duration}ms.", workerId, job.Id, durationMs);

            await _stateManager.UpdateStateAsync(
                job.Id,
                jobTypeName,
                JobStatus.Cancelled,
                currentAttempt,
                executionDurationMs: durationMs,
                createdBy: createdBy,
                startedAtUtc: startedAt,
                queue: queueName,
                priority: priority,
                ct: workerCt);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await HandleExceptionAsync(job, jobTypeName, workerId, currentAttempt, ex, workerCt, sw.Elapsed.TotalMilliseconds, createdBy, startedAt, queueName, priority);
        }
        finally
        {
            _activeJobTokens.TryRemove(job.Id, out _);
        }
    }

    private async Task HandleExceptionAsync(
        IChokaQJob job,
        string jobTypeName,
        string workerId,
        int currentAttempt,
        Exception ex,
        CancellationToken workerCt,
        double failedAfterMs,
        string? createdBy,
        DateTime? startedAt,
        string queueName,
        int priority)
    {
        _breaker.ReportFailure(jobTypeName);

        if (ex is OperationCanceledException)
        {
            _logger.LogWarning("[Worker {ID}] Job {JobId} was CANCELLED.", workerId, job.Id);
            await _stateManager.UpdateStateAsync(
                job.Id,
                jobTypeName,
                JobStatus.Cancelled,
                currentAttempt,
                executionDurationMs: failedAfterMs,
                createdBy: createdBy,
                startedAtUtc: startedAt,
                queue: queueName,
                priority: priority,
                ct: workerCt);
            return;
        }

        // We re-fetch here to ensure we don't retry a cancelled job
        var storageDto = await _storage.GetJobAsync(job.Id);
        if (storageDto == null || storageDto.Status == JobStatus.Cancelled) return;

        // Refresh attempt count from storage just in case
        currentAttempt = storageDto.AttemptCount;

        if (currentAttempt <= MaxRetries)
        {
            int nextAttempt = currentAttempt + 1;
            var baseDelay = RetryDelaySeconds * Math.Pow(2, currentAttempt - 1);
            var jitter = Random.Shared.Next(0, 1000);
            var totalDelayMs = (int)(baseDelay * 1000) + jitter;

            _logger.LogWarning(ex, "[Worker {ID}] Failed after {Duration}ms. Retrying in {Delay}ms (Attempt {Next}).",
                workerId, failedAfterMs, totalDelayMs, nextAttempt);

            await _storage.IncrementJobAttemptAsync(job.Id, nextAttempt);

            // Set back to Pending for retry (Duration is irrelevant here, reset)
            await _stateManager.UpdateStateAsync(
                job.Id,
                jobTypeName,
                JobStatus.Pending,
                nextAttempt,
                executionDurationMs: null,
                createdBy: createdBy,
                startedAtUtc: null,
                queue: queueName,
                priority: priority,
                ct: workerCt);

            // Reset start time for next run
            await Task.Delay(totalDelayMs, workerCt);
            await _queue.RequeueAsync(job, workerCt);
        }
        else
        {
            _logger.LogError(ex, "[Worker {ID}] FAILED permanently after {Duration}ms.", workerId, failedAfterMs);

            await _stateManager.UpdateStateAsync(
                job.Id,
                jobTypeName,
                JobStatus.Failed,
                currentAttempt,
                executionDurationMs: failedAfterMs,
                createdBy: createdBy,
                startedAtUtc: startedAt,
                queue: queueName,
                priority: priority,
                ct: workerCt);
        }
    }
}