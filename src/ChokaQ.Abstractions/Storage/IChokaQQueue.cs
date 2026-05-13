using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Abstractions.Storage;

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
    /// <param name="delay">Optional delay before the job becomes eligible for fetching.</param>
    /// <param name="idempotencyKey">
    /// Optional enqueue-deduplication key. When omitted and the job implements
    /// <c>IIdempotentJob</c>, the job's own key is used.
    /// </param>
    /// <returns>A task representing the async enqueue operation.</returns>
    /// <remarks>
    /// The <paramref name="delay"/> and <paramref name="idempotencyKey"/> parameters intentionally
    /// appear after the cancellation token for source compatibility with older positional calls.
    /// Prefer named arguments for these options: <c>delay: ...</c>, <c>idempotencyKey: ...</c>.
    /// Enqueue idempotency is a Hot-table guard: it suppresses duplicate active jobs with the same
    /// key, but once the original job leaves Hot for Archive or DLQ the same key may be enqueued
    /// again as a new logical attempt.
    /// </remarks>
    Task EnqueueAsync<TJob>(
        TJob job,
        int priority = 10,
        string queue = "default",
        string? createdBy = null,
        string? tags = null,
        CancellationToken ct = default,
        TimeSpan? delay = null,
        string? idempotencyKey = null) where TJob : IChokaQJob;
}
