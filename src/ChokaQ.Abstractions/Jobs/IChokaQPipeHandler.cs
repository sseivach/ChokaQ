namespace ChokaQ.Abstractions;

/// <summary>
/// Defines the contract for the "Pipe" mode handler.
/// Implement this interface when using ChokaQ in raw pipe mode.
/// </summary>
public interface IChokaQPipeHandler
{
    /// <summary>
    /// Handles a job execution based on its type key and raw payload.
    /// </summary>
    /// <param name="jobType">The job type key/string provided during enqueue.</param>
    /// <param name="payload">The raw JSON payload.</param>
    /// <param name="ct">Cancellation token.</param>
    Task HandleAsync(string jobType, string payload, CancellationToken ct);
}