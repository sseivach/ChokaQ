namespace ChokaQ.Core.Execution;

/// <summary>
/// Responsible for dispatching the job execution to the appropriate handler.
/// This abstraction supports different processing strategies (Bus vs. Pipe).
/// </summary>
public interface IJobDispatcher
{
    /// <summary>
    /// Dispatches the job execution.
    /// </summary>
    /// <param name="jobId">The unique ID of the job.</param>
    /// <param name="jobType">The type key or class name.</param>
    /// <param name="payload">The raw JSON payload.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DispatchAsync(string jobId, string jobType, string payload, CancellationToken ct);
}