namespace ChokaQ.Abstractions.DTOs;

public record QueueDto(
    string Name,
    bool IsPaused,
    int PendingCount,
    int ProcessingCount,
    int FailedCount
);