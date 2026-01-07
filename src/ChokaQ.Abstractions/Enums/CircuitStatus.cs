namespace ChokaQ.Abstractions.Enums;

public enum CircuitStatus
{
    /// <summary>
    /// Normal state. Execution is allowed.
    /// </summary>
    Closed = 0,

    /// <summary>
    /// Circuit is open. Execution is blocked to prevent cascading failures.
    /// </summary>
    Open = 1,

    /// <summary>
    /// Trial state. A limited number of requests are allowed to test system health.
    /// </summary>
    HalfOpen = 2
}