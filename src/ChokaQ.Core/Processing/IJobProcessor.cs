using ChokaQ.Abstractions;

namespace ChokaQ.Core.Processing;

/// <summary>
/// Encapsulates the complete lifecycle of processing a single job.
/// Handles Circuit Breaker checks, execution, success/failure reporting, and retries.
/// </summary>
public interface IJobProcessor
{
    /// <summary>
    /// Configuration: Maximum number of retries.
    /// </summary>
    int MaxRetries { get; set; }

    /// <summary>
    /// Configuration: Delay between retries in seconds.
    /// </summary>
    int RetryDelaySeconds { get; set; }

    /// <summary>
    /// Processes a single job with full resilience logic.
    /// </summary>
    /// <param name="job">The job to process.</param>
    /// <param name="workerId">The ID of the worker thread (for logging).</param>
    /// <param name="workerCt">The worker's cancellation token.</param>
    Task ProcessJobAsync(IChokaQJob job, string workerId, CancellationToken workerCt);
}