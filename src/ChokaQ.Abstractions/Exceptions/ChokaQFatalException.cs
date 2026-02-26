namespace ChokaQ.Abstractions.Exceptions;

/// <summary>
/// Throw this exception from a job handler to indicate that the error is non-transient (fatal).
/// The worker will bypass the retry policy and move the job directly to the Dead Letter Queue (DLQ).
/// </summary>
public class ChokaQFatalException : Exception
{
    public ChokaQFatalException(string message) : base(message) { }
    public ChokaQFatalException(string message, Exception innerException) : base(message, innerException) { }
}