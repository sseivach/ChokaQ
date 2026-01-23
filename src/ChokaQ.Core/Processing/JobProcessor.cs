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
    private readonly IJobDispatcher _dispatcher;
    private readonly IJobStateManager _stateManager;

    // Registry of tokens for currently running jobs to allow cancellation
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 3;

    public JobProcessor(
        IJobStorage storage,
        ILogger<JobProcessor> logger,
        ICircuitBreaker breaker,
        IJobDispatcher dispatcher,
        IJobStateManager stateManager)
    {
        _storage = storage;
        _logger = logger;
        _breaker = breaker;
        _dispatcher = dispatcher;
        _stateManager = stateManager;
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
        // 1. Validate State
        var storageDto = await _storage.GetJobAsync(jobId);
        if (storageDto?.Status == JobStatus.Cancelled)
        {
            _logger.LogInformation("[Worker {ID}] Skipping job {JobId} because it was cancelled.", workerId, jobId);
            return;
        }

        var meta = new JobMetadata(
            storageDto?.CreatedBy,
            storageDto?.Queue ?? "default",
            storageDto?.Priority ?? 10,
            storageDto?.AttemptCount ?? 1
        );

        // 2. Circuit Breaker Check
        if (!_breaker.IsExecutionPermitted(jobType))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", jobId, jobType);
            // In a real message broker we would NACK here. 
            // In our polling/memory model, we just delay slightly to avoid hot loop.
            await Task.Delay(5000, workerCt);
            return;
        }

        // 3. Mark as Processing
        var startedAt = DateTime.UtcNow;
        await UpdateStatusAsync(jobId, jobType, JobStatus.Processing, meta, startedAt: startedAt, ct: workerCt);

        // 4. Execute
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        _activeJobTokens.TryAdd(jobId, jobCts);

        var sw = Stopwatch.StartNew();
        try
        {
            await _dispatcher.DispatchAsync(jobId, jobType, payload, jobCts.Token);
            sw.Stop();

            _breaker.ReportSuccess(jobType);

            await UpdateStatusAsync(jobId, jobType, JobStatus.Succeeded, meta,
                durationMs: sw.Elapsed.TotalMilliseconds, startedAt: startedAt, ct: workerCt);

            _logger.LogInformation("[Worker {ID}] Job {JobId} done in {Duration}ms.", workerId, jobId, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            await UpdateStatusAsync(jobId, jobType, JobStatus.Cancelled, meta,
                durationMs: sw.Elapsed.TotalMilliseconds, startedAt: startedAt, ct: workerCt);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await HandleErrorAsync(ex, jobId, jobType, meta, workerId, workerCt, sw.Elapsed.TotalMilliseconds, startedAt);
        }
        finally
        {
            _activeJobTokens.TryRemove(jobId, out _);
        }
    }

    private async Task HandleErrorAsync(
        Exception ex, string jobId, string jobType, JobMetadata meta, string workerId,
        CancellationToken ct, double durationMs, DateTime startedAt)
    {
        _breaker.ReportFailure(jobType);

        // Re-fetch attempt count to be safe (in case of concurrent updates, though unlikely here)
        var currentJob = await _storage.GetJobAsync(jobId);
        if (currentJob == null || currentJob.Status == JobStatus.Cancelled) return;

        var currentAttempt = currentJob.AttemptCount;

        if (currentAttempt <= MaxRetries)
        {
            // RETRY
            var nextAttempt = currentAttempt + 1;
            var delayMs = CalculateBackoff(currentAttempt);

            _logger.LogWarning(ex, "[Worker {ID}] Failed after {Duration}ms. Retrying in {Delay}ms (Attempt {Next}).",
                workerId, durationMs, delayMs, nextAttempt);

            await _storage.IncrementJobAttemptAsync(jobId, nextAttempt);

            // We set it back to Pending so the Poller picks it up again
            // Ideally, we would set ScheduledAtUtc here to respect the delay
            await UpdateStatusAsync(jobId, jobType, JobStatus.Pending, meta with { AttemptCount = nextAttempt }, ct: ct);
        }
        else
        {
            // FAIL PERMANENTLY
            _logger.LogError(ex, "[Worker {ID}] FAILED permanently after {Duration}ms.", workerId, durationMs);

            await UpdateStatusAsync(jobId, jobType, JobStatus.Failed, meta,
                durationMs: durationMs, startedAt: startedAt, ct: ct);
        }
    }

    private int CalculateBackoff(int attempt)
    {
        var baseDelay = RetryDelaySeconds * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.Next(0, 1000);
        return (int)(baseDelay * 1000) + jitter;
    }

    private Task UpdateStatusAsync(string id, string type, JobStatus status, JobMetadata meta,
        double? durationMs = null, DateTime? startedAt = null, CancellationToken ct = default)
    {
        return _stateManager.UpdateStateAsync(
            id, type, status, meta.AttemptCount,
            executionDurationMs: durationMs,
            createdBy: meta.CreatedBy,
            startedAtUtc: startedAt,
            queue: meta.Queue,
            priority: meta.Priority,
            ct: ct
        );
    }

    // Lightweight record to pass metadata around internally
    private record JobMetadata(string? CreatedBy, string Queue, int Priority, int AttemptCount);
}