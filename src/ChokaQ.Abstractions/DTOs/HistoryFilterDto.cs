using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

public record HistoryFilterDto(
    // Range
    DateTime? FromUtc,
    DateTime? ToUtc,

    // Filters
    string? SearchTerm, // ID, Type, Tags, Error
    string? Queue,
    JobStatus? Status, // Optional strict status filter

    // Paging
    int PageNumber = 1,
    int PageSize = 100,

    // Sorting
    string SortBy = "Date", // "Date", "Duration", "Priority", "Type"
    bool SortDescending = true
);