namespace ChokaQ.Abstractions.Enums;

/// <summary>
/// Circuit breaker states following the standard circuit breaker pattern.
/// Prevents cascading failures by temporarily blocking requests to failing services.
/// </summary>
/// <remarks>
/// State transitions:
/// - Closed → Open: After failure threshold exceeded
/// - Open → HalfOpen: After reset timeout expires
/// - HalfOpen → Closed: If test request succeeds
/// - HalfOpen → Open: If test request fails
/// </remarks>
public enum CircuitStatus
{
    /// <summary>
    /// Normal operating state. All executions are allowed.
    /// Failure counter is active and tracking errors.
    /// </summary>
    Closed = 0,

    /// <summary>
    /// Failure threshold exceeded. All executions are blocked.
    /// System waits for ResetTimeout before transitioning to HalfOpen.
    /// </summary>
    Open = 1,

    /// <summary>
    /// Recovery testing state. Limited executions allowed to probe system health.
    /// Success transitions to Closed; failure returns to Open.
    /// </summary>
    HalfOpen = 2
}