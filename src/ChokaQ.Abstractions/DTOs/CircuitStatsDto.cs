using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

public record CircuitStatsDto(
    string JobType,
    CircuitStatus Status,
    int FailureCount,
    DateTime? ResetAtUtc
);