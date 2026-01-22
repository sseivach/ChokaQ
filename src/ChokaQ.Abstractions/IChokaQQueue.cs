namespace ChokaQ.Abstractions;

/// <summary>
/// Defines the contract for the job queueing mechanism.
/// Responsible for accepting new jobs and dispatching them for processing.
/// </summary>
public interface IChokaQQueue
{
    /// <summary>
    /// Enqueues a job for asynchronous background processing.
    /// </summary>
    /// <typeparam name="TJob">The type of the job payload, must implement <see cref="IChokaQJob"/>.</typeparam>
    /// <param name="job">The job instance to process.</param>
    /// <param name="priority">Execution priority (higher values run first).</param>
    /// <param name="createdBy">The name of the user or service initiating the job.</param>
    /// <param name="tags">Comma-separated tags for filtering.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the async enqueue operation.</returns>
    Task EnqueueAsync<TJob>(
        TJob job,
        int priority = 10,
        string queue = "default", // <--- NEW PARAMETER
        string? createdBy = null,
        string? tags = null,
        CancellationToken ct = default) where TJob : IChokaQJob;
}