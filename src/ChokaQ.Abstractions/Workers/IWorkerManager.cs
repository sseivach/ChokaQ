namespace ChokaQ.Abstractions.Workers;

public interface IWorkerManager
{
    /// <summary>
    /// Gets the current number of active background workers.
    /// </summary>
    int ActiveWorkers { get; }

    /// <summary>
    /// Gets the total configured capacity of workers (Pool Size).
    /// </summary>
    int TotalWorkers { get; }

    /// <summary>
    /// Gets whether the worker service has entered its runtime loop.
    /// </summary>
    /// <remarks>
    /// Health checks need a process-local signal that the hosted worker actually started.
    /// Queue statistics can tell us whether work exists, but they cannot tell us whether this
    /// process is actively polling storage or draining its local work buffer.
    /// </remarks>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the most recent heartbeat emitted by the worker loop.
    /// </summary>
    /// <remarks>
    /// This is intentionally a liveness signal, not a job heartbeat. Job heartbeats belong to
    /// individual Processing rows; worker heartbeats tell the host whether the background service
    /// itself is still cycling and able to make progress.
    /// </remarks>
    DateTimeOffset? LastHeartbeatUtc { get; }

    /// <summary>
    /// Gets or sets the compatibility retry-attempt limit exposed to dashboards.
    /// </summary>
    /// <remarks>
    /// New host configuration should use ChokaQOptions.Retry.MaxAttempts. WorkerManager keeps
    /// this scalar property because dashboards need a simple runtime control surface.
    /// </remarks>
    int MaxRetries { get; set; }

    /// <summary>
    /// Gets or sets the compatibility base retry delay, in seconds, exposed to dashboards.
    /// </summary>
    /// <remarks>
    /// New host configuration should use ChokaQOptions.Retry.BaseDelay.
    /// </remarks>
    int RetryDelaySeconds { get; set; }

    /// <summary>
    /// Dynamically scales the number of workers up or down.
    /// </summary>
    /// <param name="count">The target number of workers.</param>
    void UpdateWorkerCount(int count);

    /// <summary>
    /// Requests cancellation for a specific job.
    /// </summary>
    /// <param name="actor">Authenticated operator identity used for audit fields and failure details.</param>
    Task CancelJobAsync(string jobId, string? actor = null);

    /// <summary>
    /// Restarts a job by resetting its state and pushing it back to the queue.
    /// </summary>
    /// <param name="actor">Authenticated operator identity used for audit fields.</param>
    Task RestartJobAsync(string jobId, string? actor = null);

    // Bulk Action support
    /// <param name="actor">Authenticated operator identity used for audit fields.</param>
    Task SetJobPriorityAsync(string jobId, int priority, string? actor = null);

    /// <summary>
    /// Requests cancellation for a batch of jobs.
    /// </summary>
    /// <param name="actor">Authenticated operator identity used for audit fields and failure details.</param>
    Task CancelJobsAsync(IEnumerable<string> jobIds, string? actor = null);

    /// <summary>
    /// Restarts (resurrects from DLQ) a batch of jobs.
    /// </summary>
    /// <param name="actor">Authenticated operator identity used for audit fields.</param>
    Task RestartJobsAsync(IEnumerable<string> jobIds, string? actor = null);
}
