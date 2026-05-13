namespace ChokaQ.Abstractions.Resilience;

/// <summary>
/// Defines the configuration policy for a circuit breaker.
/// </summary>
public record CircuitPolicy(
    int FailureThreshold = 5,
    int BreakDurationSeconds = 30,
    int HalfOpenMaxCalls = 1);
