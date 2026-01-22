using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChokaQ.Storage.SqlServer;

public class SqlJobWorker : BackgroundService, IWorkerManager
{
    private readonly IJobStorage _storage;
    private readonly IJobProcessor _processor;
    private readonly IJobStateManager _stateManager;
    private readonly ILogger<SqlJobWorker> _logger;
    private readonly SqlJobStorageOptions _options;

    private readonly List<(Task Task, CancellationTokenSource Cts)> _workers = new();
    private readonly object _lock = new();

    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(1);

    public int MaxRetries
    {
        get => _processor.MaxRetries;
        set => _processor.MaxRetries = value;
    }

    public int RetryDelaySeconds
    {
        get => _processor.RetryDelaySeconds;
        set => _processor.RetryDelaySeconds = value;
    }

    public int ActiveWorkers { get; private set; } = 0;

    public SqlJobWorker(
        IJobStorage storage,
        IJobProcessor processor,
        IJobStateManager stateManager,
        ILogger<SqlJobWorker> logger,
        SqlJobStorageOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new SqlJobStorageOptions();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SQL Job Worker started.");
        UpdateWorkerCount(1);
        return Task.CompletedTask;
    }

    public void UpdateWorkerCount(int targetCount)
    {
        if (targetCount < 0) targetCount = 0;
        if (targetCount > 100) targetCount = 100;

        lock (_lock)
        {
            int current = _workers.Count;

            if (targetCount > current)
            {
                int toAdd = targetCount - current;
                for (int i = 0; i < toAdd; i++)
                {
                    var cts = new CancellationTokenSource();
                    var task = Task.Run(() => WorkerLoopAsync(cts.Token), cts.Token);
                    _workers.Add((task, cts));
                }
            }
            else if (targetCount < current)
            {
                int toRemove = current - targetCount;
                for (int i = 0; i < toRemove; i++)
                {
                    var worker = _workers.Last();
                    worker.Cts.Cancel();
                    _workers.RemoveAt(_workers.Count - 1);
                }
            }

            ActiveWorkers = _workers.Count;
            _logger.LogInformation("Worker count updated. Active: {Count}", ActiveWorkers);
        }
    }

    private async Task WorkerLoopAsync(CancellationToken workerCt)
    {
        var workerId = Guid.NewGuid().ToString()[..8];

        try
        {
            while (!workerCt.IsCancellationRequested)
            {
                bool jobProcessed = false;

                try
                {
                    // [STEP 1] Traffic Control & Queue Discovery
                    var allQueues = await _storage.GetQueuesAsync(workerCt);

                    var activeQueues = allQueues
                        .Where(q => !q.IsPaused)
                        .Select(q => q.Name)
                        .ToArray();

                    // [STEP 2] The Red Light Check 🔴
                    if (activeQueues.Length == 0)
                    {
                        // Logging for debug purposes
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("[Worker {ID}] No active queues found. Sleeping...", workerId);
                        }

                        await Task.Delay(5000, workerCt);
                        continue;
                    }

                    // [STEP 3] Fetch & Lock
                    var batch = await _storage.FetchAndLockNextBatchAsync(workerId, 1, activeQueues, workerCt);
                    var jobDto = batch.FirstOrDefault();

                    if (jobDto != null)
                    {
                        jobProcessed = true;

                        // 4. Deserialize payload
                        var jobType = Type.GetType(jobDto.Type);

                        if (jobType != null)
                        {
                            var jobObject = JsonSerializer.Deserialize(jobDto.Payload, jobType) as IChokaQJob;

                            if (jobObject != null)
                            {
                                // 5. Process
                                await _processor.ProcessJobAsync(jobObject, workerId, workerCt);
                            }
                            else
                            {
                                _logger.LogError("Failed to deserialize job {JobId} payload.", jobDto.Id);

                                await _stateManager.UpdateStateAsync(
                                    jobDto.Id,
                                    jobDto.Type,
                                    JobStatus.Failed,
                                    jobDto.AttemptCount,
                                    executionDurationMs: null,
                                    createdBy: jobDto.CreatedBy,
                                    startedAtUtc: null,
                                    queue: jobDto.Queue,
                                    priority: jobDto.Priority,
                                    ct: workerCt);
                            }
                        }
                        else
                        {
                            _logger.LogError("Unknown job type: {Type} for job {JobId}.", jobDto.Type, jobDto.Id);

                            await _stateManager.UpdateStateAsync(
                                jobDto.Id,
                                jobDto.Type,
                                JobStatus.Failed,
                                jobDto.AttemptCount,
                                executionDurationMs: null,
                                createdBy: jobDto.CreatedBy,
                                startedAtUtc: null,
                                queue: jobDto.Queue,
                                priority: jobDto.Priority,
                                ct: workerCt);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Worker {ID}] Error in polling loop.", workerId);
                    await Task.Delay(5000, workerCt);
                }

                // 6. Backoff Strategy
                if (!jobProcessed)
                {
                    await Task.Delay(_pollingInterval, workerCt);
                }
            }
        }
        finally
        {
            _logger.LogInformation("[Worker {ID}] Stopped.", workerId);
        }
    }

    public async Task CancelJobAsync(string jobId)
    {
        if (_processor is JobProcessor concreteProcessor)
        {
            concreteProcessor.CancelJob(jobId);
        }

        _logger.LogInformation("Marking job {JobId} as Cancelled.", jobId);

        var job = await _storage.GetJobAsync(jobId);
        if (job == null) return;

        await _stateManager.UpdateStateAsync(
            jobId,
            job.Type,
            JobStatus.Cancelled,
            job.AttemptCount,
            executionDurationMs: null,
            createdBy: job.CreatedBy,
            startedAtUtc: job.StartedAtUtc,
            queue: job.Queue,
            priority: job.Priority
        );
    }

    public async Task RestartJobAsync(string jobId)
    {
        var storageDto = await _storage.GetJobAsync(jobId);
        if (storageDto == null) return;

        if (storageDto.Status == JobStatus.Processing)
        {
            _logger.LogWarning("Cannot restart job {JobId} because it is currently Processing.", jobId);
            return;
        }

        try
        {
            var jobType = Type.GetType(storageDto.Type);
            if (jobType == null) throw new InvalidOperationException($"Cannot load type '{storageDto.Type}'.");

            _logger.LogInformation("Restarting job {JobId}...", jobId);

            await _stateManager.UpdateStateAsync(
                jobId,
                jobType.Name,
                JobStatus.Pending,
                0,
                executionDurationMs: null,
                createdBy: storageDto.CreatedBy,
                startedAtUtc: null,
                queue: storageDto.Queue,
                priority: storageDto.Priority
            );

            await _storage.IncrementJobAttemptAsync(jobId, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart job {JobId}.", jobId);
        }
    }

    public async Task SetJobPriorityAsync(string jobId, int priority)
    {
        _logger.LogInformation("Updating priority for Job {JobId} to {Priority}.", jobId, priority);
        await _storage.UpdateJobPriorityAsync(jobId, priority);
    }
}