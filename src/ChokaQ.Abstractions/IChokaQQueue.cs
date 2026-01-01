namespace ChokaQ.Abstractions;

public interface IChokaQQueue
{
    /// <summary>
    /// Pushes a job into the queue.
    /// </summary>
    Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : IChokaQJob;
}