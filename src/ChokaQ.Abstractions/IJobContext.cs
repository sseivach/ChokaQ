namespace ChokaQ.Abstractions;

/// <summary>
/// Provides context and tools for the currently executing job.
/// Allows handlers to report progress or check job ID without knowing about the worker.
/// </summary>
public interface IJobContext
{
    string JobId { get; }

    /// <summary>
    /// Reports execution progress (0-100) to the dashboard.
    /// </summary>
    Task ReportProgressAsync(int percentage);
}