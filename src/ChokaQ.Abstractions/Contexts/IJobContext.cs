namespace ChokaQ.Abstractions.Contexts;

/// <summary>
/// Provides context and tools for the currently executing job.
/// Allows handlers to report progress or check job ID without knowing about the worker.
/// </summary>
public interface IJobContext
{
    string JobId { get; }

    /// <summary>
    /// Token for the current job execution, cancelled on timeout, admin cancellation,
    /// or worker shutdown.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Reports execution progress (0-100) to the dashboard.
    /// </summary>
    Task ReportProgressAsync(int percentage);
}
