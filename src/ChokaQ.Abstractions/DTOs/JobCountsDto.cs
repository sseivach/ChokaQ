namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Global counters for the dashboard stats cards.
/// </summary>
public record JobCountsDto(
    int Pending,
    int Processing,
    int Succeeded,
    int Failed,
    int Retrying,
    int Scheduled = 0,
    int Zombies = 0
);