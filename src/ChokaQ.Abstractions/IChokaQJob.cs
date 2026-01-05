namespace ChokaQ.Abstractions;

/// <summary>
/// Represents the fundamental unit of work in ChokaQ.
/// Any class intended to be processed in the background must implement this interface.
/// </summary>
public interface IChokaQJob
{
    /// <summary>
    /// Gets the unique identifier for the job instance.
    /// This ID is used for tracking, logging, and status updates.
    /// </summary>
    string Id { get; }
}