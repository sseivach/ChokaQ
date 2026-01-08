using ChokaQ.Abstractions;

namespace ChokaQ.Core.Workers;

/// <summary>
/// Responsible for the actual execution of a job within a dedicated dependency injection scope.
/// Handles resolution of the correct handler and invocation via reflection.
/// </summary>
public interface IJobExecutor
{
    /// <summary>
    /// Executes the provided job using the registered handler.
    /// </summary>
    /// <param name="job">The job instance to process.</param>
    /// <param name="ct">Cancellation token for the execution.</param>
    Task ExecuteJobAsync(IChokaQJob job, CancellationToken ct);
}