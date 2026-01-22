namespace ChokaQ.Abstractions.DTOs;

public record QueueDto(
    string Name,
    bool IsPaused,
    int PendingCount,
    int ProcessingCount,
    int FailedCount,
    int SucceededCount,
    DateTime? FirstJobAtUtc,
    DateTime? LastJobAtUtc
);