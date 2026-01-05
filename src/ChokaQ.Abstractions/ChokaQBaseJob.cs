namespace ChokaQ.Abstractions;

/// <summary>
/// Base implementation for jobs that automatically handles unique ID generation.
/// </summary>
public abstract record ChokaQBaseJob : IChokaQJob
{
    /// <summary>
    /// Unique identifier, auto-generated upon instantiation.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();
}