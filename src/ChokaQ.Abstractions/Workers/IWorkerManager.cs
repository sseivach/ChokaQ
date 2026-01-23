namespace ChokaQ.Abstractions;

public interface IWorkerManager
{
    /// <summary>
    /// Gets the current number of active background workers.
    /// </summary>
    int ActiveWorkers { get; }

    /// <summary>
    /// Gets or sets the maximum number of retries allowed for a failed job.
    /// </summary>
    int MaxRetries { get; set; }

    /// <summary>
    /// Gets or sets the delay (in seconds) between retry attempts.
    /// </summary>
    int RetryDelaySeconds { get; set; }

    /// <summary>
    /// Dynamically scales the number of workers up or down.
    /// </summary>
    /// <param name="count">The target number of workers.</param>
    void UpdateWorkerCount(int count);

    /// <summary>
    /// Requests cancellation for a specific job.
    /// </summary>
    Task CancelJobAsync(string jobId);

    /// <summary>
    /// Restarts a job by resetting its state and pushing it back to the queue.
    /// </summary>
    Task RestartJobAsync(string jobId);

    // Bulk Action support
    Task SetJobPriorityAsync(string jobId, int priority);
}