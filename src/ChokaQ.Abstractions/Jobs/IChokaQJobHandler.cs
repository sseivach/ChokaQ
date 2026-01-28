namespace ChokaQ.Abstractions;

/// <summary>
/// Defines the execution logic for a specific job type in Bus Mode.
/// Each job type requires exactly one handler registered via ChokaQJobProfile.
/// </summary>
/// <typeparam name="TJob">The job DTO type containing execution parameters.</typeparam>
/// <remarks>
/// Implementation guidelines:
/// - Handlers are resolved from DI container (scoped lifetime per job)
/// - Check CancellationToken regularly for graceful shutdown
/// - Throw exceptions to trigger retry logic
/// - Use IJobContext for progress reporting
/// 
/// Example:
/// <code>
/// public class SendEmailHandler : IChokaQJobHandler&lt;SendEmailJob&gt;
/// {
///     public async Task HandleAsync(SendEmailJob job, CancellationToken ct)
///     {
///         await _emailService.SendAsync(job.To, job.Subject, job.Body, ct);
///     }
/// }
/// </code>
/// </remarks>
public interface IChokaQJobHandler<in TJob> where TJob : IChokaQJob
{
    /// <summary>
    /// Executes the job logic.
    /// </summary>
    /// <param name="job">The deserialized job payload.</param>
    /// <param name="ct">Cancellation token for cooperative shutdown.</param>
    Task HandleAsync(TJob job, CancellationToken ct);
}