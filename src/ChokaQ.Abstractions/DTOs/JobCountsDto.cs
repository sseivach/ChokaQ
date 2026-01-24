namespace ChokaQ.Abstractions.DTOs;

public record JobCountsDto(
    int Pending,
    int Fetched,
    int Processing,
    int Succeeded,
    int Failed,
    int Cancelled,
    int Total
);