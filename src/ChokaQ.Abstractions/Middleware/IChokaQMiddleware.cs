using ChokaQ.Abstractions.Contexts;

namespace ChokaQ.Abstractions.Middleware;

/// <summary>
/// Represents a function that can process a job in the pipeline.
/// </summary>
public delegate Task JobDelegate();

/// <summary>
/// Defines middleware that can be added to the job execution pipeline.
/// Used for cross-cutting concerns like logging, validation, metrics, etc.
/// </summary>
public interface IChokaQMiddleware
{
    /// <summary>
    /// Executes the middleware.
    /// </summary>
    /// <param name="context">Context for the currently executing job.</param>
    /// <param name="job">The deserialized job instance (Bus Mode) or raw payload string (Pipe Mode).</param>
    /// <param name="next">The delegate representing the remaining middleware in the pipeline.</param>
    Task InvokeAsync(IJobContext context, object? job, JobDelegate next);
}