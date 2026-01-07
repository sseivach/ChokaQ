namespace ChokaQ.Core.Workers;

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
    /// If the job is running, it triggers the CancellationToken.
    /// If pending, updates the state in storage so it gets skipped.
    /// </summary>
    Task CancelJobAsync(string jobId);
}