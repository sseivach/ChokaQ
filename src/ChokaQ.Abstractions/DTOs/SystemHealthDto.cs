using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Captures the dashboard-facing operational health snapshot.
/// </summary>
/// <remarks>
/// A dashboard should read one coherent snapshot instead of composing health from many
/// unrelated calls. That keeps the UI simple and, more importantly, keeps storage owners
/// free to optimize the query plan without changing the control-plane contract.
/// </remarks>
public sealed record SystemHealthDto(
    DateTime GeneratedAtUtc,
    IReadOnlyList<QueueHealthDto> Queues,
    double JobsPerSecondLastMinute,
    double JobsPerSecondLastFiveMinutes,
    double FailureRateLastMinutePercent,
    double FailureRateLastFiveMinutesPercent,
    IReadOnlyList<DlqErrorGroupDto> TopErrors);

/// <summary>
/// Per-queue saturation data derived from eligible Pending jobs.
/// </summary>
/// <remarks>
/// Queue lag is a better saturation signal than queue depth. Ten tiny jobs and ten
/// million-dollar settlement jobs both count as "10", but lag shows whether workers are
/// keeping up with the queue's real arrival pattern.
/// </remarks>
public sealed record QueueHealthDto(
    string Queue,
    int Pending,
    double AverageLagSeconds,
    double MaxLagSeconds);

/// <summary>
/// A grouped DLQ failure family for quick operator triage.
/// </summary>
/// <remarks>
/// The prefix intentionally groups by the first normalized part of the error message instead
/// of the full stack trace. Full traces usually contain volatile line numbers and job-specific
/// values, which would explode cardinality and hide the actual repeated failure families.
/// </remarks>
public sealed record DlqErrorGroupDto(
    FailureReason FailureReason,
    string ErrorPrefix,
    long Count,
    DateTime LatestFailedAtUtc);
