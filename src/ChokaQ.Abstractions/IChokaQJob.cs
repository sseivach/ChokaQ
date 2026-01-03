namespace ChokaQ.Abstractions;

/// <summary>
/// Marker interface. Any class implementing this interface can be processed by ChokaQ.
/// </summary>
public interface IChokaQJob
{
    /// <summary>
    /// Unique identifier for the job instance.
    /// Must be globally unique (Guid).
    /// </summary>
    string Id { get; }
}