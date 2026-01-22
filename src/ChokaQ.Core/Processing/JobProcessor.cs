using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Execution;
using ChokaQ.Core.State;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ChokaQ.Core.Processing;

public class JobProcessor : IJobProcessor
{
    private readonly IJobStorage _storage;
    private readonly ILogger<JobProcessor> _logger;
    private readonly ICircuitBreaker _breaker;
    private readonly IJobDispatcher _dispatcher; // Changed from IJobExecutor
    private readonly IJobStateManager _stateManager;
    private readonly ChokaQ.Core.Defaults.InMemoryQueue _queue;

    // Registry of tokens for currently running jobs to allow cancellation
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 3;

    public JobProcessor(
        IJobStorage storage,
        ILogger<JobProcessor> logger,
        ICircuitBreaker breaker,
        IJobDispatcher dispatcher,
        IJobStateManager stateManager,
        ChokaQ.Core.Defaults.InMemoryQueue queue)
    {
        _storage = storage;
        _logger = logger;
        _breaker = breaker;
        _dispatcher = dispatcher;
        _stateManager = stateManager;
        _queue = queue;
    }

    public void CancelJob(string jobId)
    {
        if (_activeJobTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogInformation("Requesting cancellation for running job {JobId}...", jobId);
            cts.Cancel();
        }
    }

    public async Task ProcessJobAsync(string jobId, string jobType, string payload, string workerId, CancellationToken workerCt)
    {
        // We re-fetch state to ensure it wasn't cancelled while in queue
        var storageDto = await _storage.GetJobAsync(jobId);

        if (storageDto?.Status == JobStatus.Cancelled)
        {
            _logger.LogInformation("[Worker {ID}] Skipping job {JobId} because it was cancelled.", workerId, jobId);
            return;
        }

        var createdBy = storageDto?.CreatedBy;
        var queueName = storageDto?.Queue ?? "default";
        var priority = storageDto?.Priority ?? 10;
        int currentAttempt = storageDto?.AttemptCount ?? 1;

        // [STEP 1] Check Circuit Breaker
        if (!_breaker.IsExecutionPermitted(jobType))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", jobId, jobType);
            await Task.Delay(5000, workerCt);
            // Requeue (logic assumes In-Memory for now, SQL polling handles re-fetch naturally, but we push back to channel for memory queue)
            // Note: For Pipe mode, we need to construct a "fake" job object if using InMemoryQueue's typed Requeue, 
            // OR we update InMemoryQueue to accept raw. 
            // For now, assuming SQL Polling loop, re-queueing is implicit by not marking success, 
            // but here we are explicit.

            // TODO: Revisit Requeue logic for Pipe Mode in InMemoryQueue. 
            return;
        }

        // Set status to Processing
        var startedAt = DateTime.UtcNow;
        await _stateManager.UpdateStateAsync(
            jobId,
            jobType,
            JobStatus.Processing,
            currentAttempt,
            executionDurationMs: null,
            createdBy: createdBy,
            startedAtUtc: startedAt,
            queue: queueName,
            priority: priority,
            ct: workerCt);

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        _activeJobTokens.TryAdd(jobId, jobCts);

        var sw = Stopwatch.StartNew();

        try
        {
            // DELEGATE TO DISPATCHER (Strategy Pattern)
            await _dispatcher.DispatchAsync(jobId, jobType, payload, jobCts.Token);

            sw.Stop();
            var durationMs = sw.Elapsed.TotalMilliseconds;

            // Report Success
            _breaker.ReportSuccess(jobType);

            await _stateManager.UpdateStateAsync(
                jobId,
                jobType,
                JobStatus.Succeeded,
                currentAttempt,
                executionDurationMs: durationMs,
                createdBy: createdBy,
                startedAtUtc: startedAt,
                queue: queueName,
                priority: priority,
                ct: workerCt);

            _logger.LogInformation("[Worker {ID}] Job {JobId} done in {Duration}ms.", workerId, jobId, durationMs);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var durationMs = sw.Elapsed.TotalMilliseconds;
            _logger.LogWarning("[Worker {ID}] Job {JobId} was CANCELLED after {Duration}ms.", workerId, jobId, durationMs);

            await _stateManager.UpdateStateAsync(
                jobId,
                jobType,
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
            await HandleExceptionAsync(jobId, jobType, payload, workerId, currentAttempt, ex, workerCt, sw.Elapsed.TotalMilliseconds, createdBy, startedAt, queueName, priority);
        }
        finally
        {
            _activeJobTokens.TryRemove(jobId, out _);
        }
    }

    private async Task HandleExceptionAsync(
        string jobId,
        string jobType,
        string payload,
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
        _breaker.ReportFailure(jobType);

        if (ex is OperationCanceledException)
        {
            // Already handled above, but double check
            return;
        }

        var storageDto = await _storage.GetJobAsync(jobId);
        if (storageDto == null || storageDto.Status == JobStatus.Cancelled) return;

        currentAttempt = storageDto.AttemptCount;

        if (currentAttempt <= MaxRetries)
        {
            int nextAttempt = currentAttempt + 1;
            var baseDelay = RetryDelaySeconds * Math.Pow(2, currentAttempt - 1);
            var jitter = Random.Shared.Next(0, 1000);
            var totalDelayMs = (int)(baseDelay * 1000) + jitter;

            _logger.LogWarning(ex, "[Worker {ID}] Failed after {Duration}ms. Retrying in {Delay}ms (Attempt {Next}).",
                workerId, failedAfterMs, totalDelayMs, nextAttempt);

            await _storage.IncrementJobAttemptAsync(jobId, nextAttempt);

            await _stateManager.UpdateStateAsync(
                jobId,
                jobType,
                JobStatus.Pending,
                nextAttempt,
                executionDurationMs: null,
                createdBy: createdBy,
                startedAtUtc: null,
                queue: queueName,
                priority: priority,
                ct: workerCt);

            // Backoff logic is handled by the Poller picking it up later, 
            // or explicit delay if we keep it in memory.
            // For now, we update DB state, so Poller will pick it up eventually (or immediately if we don't set ScheduledAt).
            // To properly delay, we should likely update ScheduledAt in DB. 
            // For this iteration, we rely on the state update.
        }
        else
        {
            _logger.LogError(ex, "[Worker {ID}] FAILED permanently after {Duration}ms.", workerId, failedAfterMs);

            await _stateManager.UpdateStateAsync(
                jobId,
                jobType,
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