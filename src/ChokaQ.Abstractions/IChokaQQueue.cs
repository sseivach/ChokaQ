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
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the async enqueue operation.</returns>
    Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : IChokaQJob;
}