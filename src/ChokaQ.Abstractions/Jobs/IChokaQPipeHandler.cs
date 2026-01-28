namespace ChokaQ.Abstractions;

/// <summary>
/// Global handler contract for Pipe Mode (high-throughput event processing).
/// All jobs are routed to a single handler without type-specific deserialization.
/// </summary>
/// <remarks>
/// Use Pipe Mode when:
/// - Processing high-volume event streams
/// - Job types are dynamic or unknown at compile time
/// - You need maximum throughput with minimal overhead
/// - Using external dispatch logic (e.g., routing based on payload content)
/// 
/// Pipe Mode vs Bus Mode:
/// - Bus Mode: Type-safe handlers, automatic DI, ChokaQJobProfile configuration
/// - Pipe Mode: Single handler, raw payloads, manual dispatch, maximum performance
/// 
/// Example:
/// <code>
/// public class GlobalPipeHandler : IChokaQPipeHandler
/// {
///     public async Task HandleAsync(string jobType, string payload, CancellationToken ct)
///     {
///         switch (jobType)
///         {
///             case "email": await ProcessEmail(payload, ct); break;
///             case "sms": await ProcessSms(payload, ct); break;
///             default: throw new NotSupportedException($"Unknown job type: {jobType}");
///         }
///     }
/// }
/// </code>
/// </remarks>
public interface IChokaQPipeHandler
{
    /// <summary>
    /// Processes a job with raw type key and JSON payload.
    /// </summary>
    /// <param name="jobType">Job type identifier (stored in Type column).</param>
    /// <param name="payload">Raw JSON payload from Payload column.</param>
    /// <param name="ct">Cancellation token for cooperative shutdown.</param>
    Task HandleAsync(string jobType, string payload, CancellationToken ct);
}