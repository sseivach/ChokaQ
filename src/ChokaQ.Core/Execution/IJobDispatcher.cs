using ChokaQ.Abstractions.Entities;

namespace ChokaQ.Core.Execution;

/// <summary>
/// Responsible for dispatching the job execution to the appropriate handler.
/// Supports different processing strategies (Bus vs. Pipe).
/// </summary>
public interface IJobDispatcher
{
    /// <summary>
    /// Executes the business logic for the given job entity.
    /// </summary>
    Task ExecuteAsync(JobEntity job, CancellationToken ct);
}