using ChokaQ.Abstractions;
using System.Threading;
using System.Threading.Tasks;

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
    /// Configuration: Time to wait (in seconds) when the Circuit Breaker blocks execution.
    /// Default: 5 seconds.
    /// </summary>
    int CircuitBreakerDelaySeconds { get; set; }

    /// <summary>
    /// Processes a single job with full resilience logic.
    /// </summary>
    /// <param name="jobId">Unique Job ID.</param>
    /// <param name="jobType">Type key or class name.</param>
    /// <param name="payload">JSON payload.</param>
    /// <param name="workerId">The ID of the worker thread.</param>
    /// <param name="workerCt">The worker's cancellation token.</param>
    Task ProcessJobAsync(string jobId, string jobType, string payload, string workerId, CancellationToken workerCt);
}