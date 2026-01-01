namespace ChokaQ.Abstractions;

/// <summary>
/// Defines logic to process a specific job type.
/// </summary>
/// <typeparam name="TJob">The type of job this handler processes.</typeparam>
public interface IChokaQJobHandler<in TJob> where TJob : IChokaQJob
{
    Task HandleAsync(TJob job, CancellationToken ct);
}