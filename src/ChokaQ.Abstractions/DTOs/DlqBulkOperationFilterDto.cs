using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Describes a bounded operator action over a subset of DLQ jobs.
/// </summary>
/// <remarks>
/// Bulk DLQ operations are intentionally filter-based instead of "delete everything" commands.
/// Production operators need to act on a failure family, queue, or job type, but the storage layer
/// still needs a hard maximum so one mistaken click cannot purge or requeue an unbounded DLQ.
/// </remarks>
public sealed record DlqBulkOperationFilterDto(
    string? Queue = null,
    FailureReason? FailureReason = null,
    string? Type = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? SearchTerm = null,
    int MaxJobs = DlqBulkOperationFilterDto.DefaultMaxJobs)
{
    public const int DefaultMaxJobs = 100;
    public const int AbsoluteMaxJobs = 1000;
}
