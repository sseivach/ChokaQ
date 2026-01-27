using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Circuit breaker statistics for a job type.
/// Used for resilience monitoring in the dashboard.
/// </summary>
public record CircuitStatsDto(
    string JobType,
    CircuitStatus Status,
    int FailureCount,
    DateTime? ResetAtUtc
);
