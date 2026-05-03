using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Observability;

/// <summary>
/// Stable event identifiers for ChokaQ structured logging.
/// </summary>
/// <remarks>
/// Message templates help humans read logs, but production systems often route, alert, and
/// correlate on numeric EventId values. Keep these IDs stable once published. Add new IDs for
/// new events instead of reusing old numbers with different meanings.
///
/// Ranges:
/// - 1000-1099: worker service and loop lifecycle.
/// - 2000-2099: individual job execution lifecycle.
/// - 2100-2199: persisted state transition and notification plumbing.
/// - 3000-3099: enqueue producer boundary.
/// - 4000-4099: recovery and zombie rescue.
/// - 5000-5099: SQL provisioning and storage boundary.
/// - 6000-6099: operator/admin command boundary.
/// </remarks>
public static class ChokaQLogEvents
{
    public static readonly EventId WorkerStarted = new(1000, nameof(WorkerStarted));
    public static readonly EventId WorkerStopped = new(1001, nameof(WorkerStopped));
    public static readonly EventId WorkerLoopCrashed = new(1002, nameof(WorkerLoopCrashed));
    public static readonly EventId WorkerLoopStoppedUnexpectedly = new(1003, nameof(WorkerLoopStoppedUnexpectedly));
    public static readonly EventId WorkerCapacityChanged = new(1004, nameof(WorkerCapacityChanged));
    public static readonly EventId PrefetchedJobReleased = new(1010, nameof(PrefetchedJobReleased));
    public static readonly EventId PrefetchedJobReleaseFailed = new(1011, nameof(PrefetchedJobReleaseFailed));
    public static readonly EventId ProcessingTaskDrainStarted = new(1012, nameof(ProcessingTaskDrainStarted));
    public static readonly EventId ProcessingTaskShutdownObserved = new(1013, nameof(ProcessingTaskShutdownObserved));
    public static readonly EventId ProcessingTaskWrapperFailed = new(1014, nameof(ProcessingTaskWrapperFailed));

    public static readonly EventId JobCancellationRequested = new(2000, nameof(JobCancellationRequested));
    public static readonly EventId CircuitBreakerRejected = new(2001, nameof(CircuitBreakerRejected));
    public static readonly EventId JobExecutionLeaseRejected = new(2002, nameof(JobExecutionLeaseRejected));
    public static readonly EventId JobSucceededArchived = new(2003, nameof(JobSucceededArchived));
    public static readonly EventId JobShutdownRescheduled = new(2004, nameof(JobShutdownRescheduled));
    public static readonly EventId JobTimedOutOrCancelled = new(2005, nameof(JobTimedOutOrCancelled));
    public static readonly EventId JobHeartbeatWriteFailed = new(2006, nameof(JobHeartbeatWriteFailed));
    public static readonly EventId JobHeartbeatThresholdReached = new(2007, nameof(JobHeartbeatThresholdReached));
    public static readonly EventId JobFatalErrorDlq = new(2020, nameof(JobFatalErrorDlq));
    public static readonly EventId JobThrottledRetryScheduled = new(2021, nameof(JobThrottledRetryScheduled));
    public static readonly EventId JobTransientRetryScheduled = new(2022, nameof(JobTransientRetryScheduled));
    public static readonly EventId JobRetriesExhaustedDlq = new(2023, nameof(JobRetriesExhaustedDlq));

    public static readonly EventId StateTransitionNotApplied = new(2100, nameof(StateTransitionNotApplied));
    public static readonly EventId NotificationFailed = new(2110, nameof(NotificationFailed));

    public static readonly EventId EnqueueDuplicateSkipped = new(3000, nameof(EnqueueDuplicateSkipped));
    public static readonly EventId EnqueueNotificationFailed = new(3001, nameof(EnqueueNotificationFailed));

    public static readonly EventId ZombieRescueStarted = new(4000, nameof(ZombieRescueStarted));
    public static readonly EventId AbandonedJobsRecovered = new(4001, nameof(AbandonedJobsRecovered));
    public static readonly EventId ZombieJobsArchived = new(4002, nameof(ZombieJobsArchived));
    public static readonly EventId ZombieRescueCycleFailed = new(4003, nameof(ZombieRescueCycleFailed));

    public static readonly EventId SqlInitializationStarted = new(5000, nameof(SqlInitializationStarted));
    public static readonly EventId SqlInitializationCompleted = new(5001, nameof(SqlInitializationCompleted));
    public static readonly EventId SqlInitializationFailed = new(5002, nameof(SqlInitializationFailed));
    public static readonly EventId SqlScriptExecutionFailed = new(5003, nameof(SqlScriptExecutionFailed));

    public static readonly EventId AdminCommandRejected = new(6000, nameof(AdminCommandRejected));
    public static readonly EventId AdminCommandCompleted = new(6001, nameof(AdminCommandCompleted));

    /// <summary>
    /// Returns the published ChokaQ event catalog for tests and generated documentation.
    /// </summary>
    public static IReadOnlyList<EventId> All { get; } = new[]
    {
        WorkerStarted,
        WorkerStopped,
        WorkerLoopCrashed,
        WorkerLoopStoppedUnexpectedly,
        WorkerCapacityChanged,
        PrefetchedJobReleased,
        PrefetchedJobReleaseFailed,
        ProcessingTaskDrainStarted,
        ProcessingTaskShutdownObserved,
        ProcessingTaskWrapperFailed,
        JobCancellationRequested,
        CircuitBreakerRejected,
        JobExecutionLeaseRejected,
        JobSucceededArchived,
        JobShutdownRescheduled,
        JobTimedOutOrCancelled,
        JobHeartbeatWriteFailed,
        JobHeartbeatThresholdReached,
        JobFatalErrorDlq,
        JobThrottledRetryScheduled,
        JobTransientRetryScheduled,
        JobRetriesExhaustedDlq,
        StateTransitionNotApplied,
        NotificationFailed,
        EnqueueDuplicateSkipped,
        EnqueueNotificationFailed,
        ZombieRescueStarted,
        AbandonedJobsRecovered,
        ZombieJobsArchived,
        ZombieRescueCycleFailed,
        SqlInitializationStarted,
        SqlInitializationCompleted,
        SqlInitializationFailed,
        SqlScriptExecutionFailed,
        AdminCommandRejected,
        AdminCommandCompleted
    };
}
