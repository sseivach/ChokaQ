namespace ChokaQ.Abstractions;

public interface IChokaQJobHandler<in TJob> where TJob : IChokaQJob
{
    Task HandleAsync(TJob job, CancellationToken ct);
}