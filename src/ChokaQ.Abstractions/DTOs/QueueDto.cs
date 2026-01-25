namespace ChokaQ.Abstractions.DTOs;

public record QueueDto(
    string Name,
    bool IsPaused,
    int PendingCount,
    int FetchedCount,
    int ProcessingCount,
    int FailedCount,
    int SucceededCount,
    int CancelledCount,
    DateTime? FirstJobAtUtc,
    DateTime? LastJobAtUtc
);