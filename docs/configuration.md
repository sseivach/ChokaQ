# Runtime Configuration

ChokaQ is designed for future NuGet packaging, so production hosts should be able to keep operational policy in `appsettings.json`, environment variables, Key Vault-backed configuration, or any other `IConfiguration` provider.

The code defaults are intentionally conservative. They make a local demo work without configuration, but every important timeout and retry policy can be made explicit by the host application.

## Recommended Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddChokaQ(
    builder.Configuration.GetSection("ChokaQ"),
    options =>
    {
        // Profiles and middleware are compile-time registrations.
        // Runtime policy comes from appsettings unless code intentionally overrides it here.
        options.AddProfile<MailingProfile>();
        options.AddMiddleware<CorrelationMiddleware>();
    });

builder.Services.UseSqlServer(
    builder.Configuration.GetSection("ChokaQ:SqlServer"));

builder.Services.AddHealthChecks()
    .AddChokaQSqlServerHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));
```

Binding order is:

1. ChokaQ code defaults.
2. `IConfiguration` values.
3. Optional code callback overrides.
4. Per-queue runtime overrides where supported.

This order lets platform teams keep environment policy in configuration while application code still owns type-safe registrations such as profiles, handlers, and middleware.

## appsettings.json

```json
{
  "ChokaQ": {
    "Execution": {
      "DefaultTimeout": "00:15:00",
      "HeartbeatIntervalMin": "00:00:08",
      "HeartbeatIntervalMax": "00:00:12",
      "HeartbeatFailureThreshold": 3
    },
    "Retry": {
      "MaxAttempts": 3,
      "BaseDelay": "00:00:03",
      "MaxDelay": "01:00:00",
      "BackoffMultiplier": 2.0,
      "JitterMaxDelay": "00:00:01",
      "CircuitBreakerDelay": "00:00:05"
    },
    "Recovery": {
      "FetchedJobTimeout": "00:10:00",
      "ProcessingZombieTimeout": "00:10:00",
      "ScanInterval": "00:01:00"
    },
    "Worker": {
      "PausedQueuePollingDelay": "00:00:01"
    },
    "InMemory": {
      "MaxCapacity": 100000
    },
    "Health": {
      "WorkerHeartbeatTimeout": "00:00:30",
      "QueueLagDegradedThreshold": "00:00:05",
      "QueueLagUnhealthyThreshold": "00:00:10"
    },
    "Metrics": {
      "MaxQueueTagValues": 100,
      "MaxJobTypeTagValues": 500,
      "MaxErrorTagValues": 100,
      "MaxFailureReasonTagValues": 50,
      "MaxTagValueLength": 128,
      "UnknownTagValue": "unknown",
      "OverflowTagValue": "other"
    },
    "Queues": {
      "reports": {
        "ExecutionTimeout": "01:00:00"
      }
    },
    "SqlServer": {
      "ConnectionString": "Server=localhost;Database=ChokaQ;Trusted_Connection=True;TrustServerCertificate=True;",
      "SchemaName": "chokaq",
      "AutoCreateSqlTable": false,
      "PollingInterval": "00:00:01",
      "NoQueuesSleepInterval": "00:00:05",
      "CommandTimeoutSeconds": 30,
      "CleanupBatchSize": 1000,
      "MaxTransientRetries": 3,
      "TransientRetryBaseDelayMs": 200
    }
  }
}
```

## Execution

`Execution.DefaultTimeout` is the hard wall-clock limit for a job handler. If user code does not finish before the timeout, ChokaQ cancels the handler token and moves the job through the timeout path.

Why this exists: a stuck handler can hold a worker lease forever. That hides queue saturation, prevents other jobs from running, and forces operators to fix the system by hand. A background processor should have an explicit execution boundary.

`Queues:{queueName}:ExecutionTimeout` overrides the global timeout for one queue. Use it for real workload differences. For example, a `reports` queue might need one hour, while the default queue should still fail fast after fifteen minutes.

## Retry

`Retry.MaxAttempts` is the maximum total number of executions, including the first try. The old `MaxRetries` property still exists for compatibility, but new configuration should use `Retry.MaxAttempts`.

`Retry.BaseDelay`, `Retry.BackoffMultiplier`, and `Retry.MaxDelay` define exponential backoff. With the defaults, retry delays start at three seconds, double after each failed attempt, and never exceed one hour.

`Retry.JitterMaxDelay` adds randomness to retry scheduling. Jitter prevents thousands of jobs from retrying at the same millisecond after a downstream outage. The jitter is clipped by `Retry.MaxDelay`, so the max delay remains a true operational cap.

`Retry.CircuitBreakerDelay` is used when the circuit breaker blocks a job type. The job is rescheduled instead of executed immediately, which reduces pressure on a dependency that is already unhealthy.

## Recovery

`Recovery.FetchedJobTimeout` applies to jobs claimed by a worker but not yet executing user code. Recovering these jobs is safe because no handler side effects have happened.

`Recovery.ProcessingZombieTimeout` applies to jobs that started user code but stopped sending heartbeats. These jobs are moved to DLQ as zombies because they may have already produced side effects and should be inspected.

`Recovery.ScanInterval` controls how often `ZombieRescueService` searches for abandoned and zombie jobs. Shorter intervals reduce time-to-recovery. Longer intervals reduce database load.

## In-Memory

`InMemory.MaxCapacity` is the soft cap for jobs retained by the in-process Three
Pillars store. The default is 100000. When the cap is reached, ChokaQ evicts old
Archive rows first and old DLQ rows second. Hot rows are preserved because they
represent accepted work that the in-memory worker still needs to process.

Why this exists: in-memory mode is useful for demos, tests, local development,
and volatile streams, but process memory is not an infinite queue. The cap keeps
historical rows from growing without bound while making the tradeoff explicit:
SQL Server mode is the production choice when backlog durability and restart
survival matter.

For the full policy, see [Backpressure Policy](/3-deep-dives/backpressure-policy).

## SQL Server

`SqlServer.PollingInterval` controls how often the SQL worker polls when active queues exist but no jobs are currently available.

`SqlServer.NoQueuesSleepInterval` controls the sleep duration when every queue is paused or inactive.

`SqlServer.CommandTimeoutSeconds` is the maximum time for each SQL command ChokaQ issues. It applies to worker fetches, state transitions, dashboard reads, recovery scans, and admin commands. It does not control user handler execution; that is `Execution.DefaultTimeout`.

`SqlServer.CleanupBatchSize` is the maximum number of Archive or DLQ rows ChokaQ deletes in one cleanup transaction. The default is 1000. Lower it when SQL Server is shared with latency-sensitive workloads or transaction-log pressure matters more than cleanup speed. Raise it when retention cleanup must catch up quickly and the database has enough log and IO headroom.

Why this exists: retention cleanup is operational maintenance, not user work. A single huge `DELETE` can hold locks, grow the transaction log, and make workers or dashboard queries wait. Batching cleanup gives administrators a simple pressure valve while keeping purge APIs easy to use.

`SqlServer.MaxTransientRetries` and `SqlServer.TransientRetryBaseDelayMs` configure retries around transient SQL failures such as deadlocks or short network blips. These retries protect storage operations, not user job handlers.

When `AutoCreateSqlTable` is enabled, ChokaQ also creates `[schema].[SchemaMigrations]`. This table records the applied ChokaQ SQL schema version. Version `1` represents the initial Three Pillars schema, version `2` records hot-path index hardening, and version `3` adds rolling `MetricBuckets` for materialized throughput and failure-rate windows. Future SQL migrations should append one row after they successfully apply, which gives operators a database-side audit trail during upgrades.

## Metrics

ChokaQ emits OpenTelemetry instruments through `System.Diagnostics.Metrics` using the meter name `ChokaQ`. The library does not install exporters; the host application decides whether those metrics go to Prometheus, OTLP, Application Insights, or another backend.

| Instrument | Type | Unit | Tags | Meaning |
|---|---|---|---|---|
| `chokaq.jobs.enqueued` | Counter | none | `queue`, `type` | Jobs accepted by storage. |
| `chokaq.jobs.completed` | Counter | none | `queue`, `type` | Jobs archived as successful. |
| `chokaq.jobs.failed` | Counter | none | `queue`, `type`, `error` | Handler executions that threw. |
| `chokaq.jobs.processing_duration` | Histogram | `ms` | `queue`, `type` | Wall-clock handler duration. |
| `chokaq.jobs.queue_lag` | Histogram | `ms` | `queue`, `type` | Time a job waited before execution. |
| `chokaq.jobs.dlq` | Counter | none | `queue`, `type`, `reason` | Jobs moved to DLQ. |
| `chokaq.jobs.retried` | Counter | none | `queue`, `type`, `attempt` | Jobs scheduled for another attempt. |
| `chokaq.workers.active` | UpDownCounter | none | `queue` | Active worker delta by queue. |

`Metrics.MaxQueueTagValues`, `Metrics.MaxJobTypeTagValues`, `Metrics.MaxErrorTagValues`, and `Metrics.MaxFailureReasonTagValues` cap the number of distinct tag values one process emits for each tag family. Once a budget is exhausted, new values are reported as `Metrics.OverflowTagValue` instead of creating new time series.

`Metrics.UnknownTagValue` replaces blank tag values. `Metrics.MaxTagValueLength` trims long values before cardinality tracking. The defaults preserve useful operator dimensions while preventing accidental unbounded cardinality.

Why this exists: labels are the dangerous part of metrics. A stable counter name is cheap; a tag that contains tenant IDs, generated queue names, exception messages, or user data can create thousands of time series and make the monitoring backend slow or expensive. ChokaQ keeps high-value dimensions but puts a hard ceiling on the damage one process can cause.

## Structured Log Events

ChokaQ emits lifecycle logs with stable `EventId` values. The message template is for humans; the event ID is for machines. Use `EventId.Id` or `EventId.Name` in SIEM queries, alert rules, and runbooks instead of searching for fragile text snippets.

| Range | Area | Examples |
|---|---|---|
| `1000-1099` | Worker service and loop lifecycle. | `WorkerStarted`, `WorkerStopped`, `WorkerLoopCrashed` |
| `2000-2099` | Individual job execution lifecycle. | `JobSucceededArchived`, `JobTimedOutOrCancelled` |
| `2100-2199` | Persisted state transitions and UI notification plumbing. | `StateTransitionNotApplied`, `NotificationFailed` |
| `3000-3099` | Enqueue producer boundary. | `EnqueueDuplicateSkipped`, `EnqueueNotificationFailed` |
| `4000-4099` | Recovery and zombie rescue. | `AbandonedJobsRecovered`, `ZombieJobsArchived` |
| `5000-5099` | SQL provisioning and storage boundary. | `SqlInitializationStarted`, `SqlInitializationFailed` |
| `6000-6099` | Operator/admin command boundary. | `AdminCommandRejected`, `AdminCommandCompleted` |

Why this exists: background processors fail across multiple layers. A job can be rejected by a circuit breaker, lose worker ownership, retry, move to DLQ, or be changed by an operator. Stable event IDs let an on-call engineer reconstruct that path even when log wording changes in a future release.

## Health Checks

ChokaQ integrates with the standard ASP.NET Core health-check pipeline. This is the right boundary for host applications and future package consumers: the host decides the route, authentication, Kubernetes probes, and monitoring integration, while ChokaQ contributes domain-specific checks.

For SQL Server mode:

```csharp
builder.Services.AddHealthChecks()
    .AddChokaQSqlServerHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

app.MapHealthChecks("/health");
```

For in-memory or pipe-only mode:

```csharp
builder.Services.AddHealthChecks()
    .AddChokaQHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

app.MapHealthChecks("/health");
```

Registered check names:

| Check | Meaning |
|---|---|
| `chokaq_sql` | SQL Server is reachable and the ChokaQ schema contains the required runtime tables. |
| `chokaq_worker` | The hosted worker is running, has capacity, and has produced a recent process-local heartbeat. |
| `chokaq_queue_saturation` | The worst pending queue lag is inside the configured operational thresholds. |

`Health.WorkerHeartbeatTimeout` is the maximum age of the worker's process-local heartbeat. This is not the same as per-job heartbeat storage: per-job heartbeats tell zombie recovery whether a specific job is still alive; the worker heartbeat tells operators whether the hosted worker loop itself is alive.

`Health.QueueLagDegradedThreshold` and `Health.QueueLagUnhealthyThreshold` map queue saturation into readiness status. The defaults match The Deck's dashboard colors: under five seconds is healthy, five to ten seconds is degraded, and above ten seconds is unhealthy.

Why this exists: queue depth alone is often misleading. A queue with 10,000 fast jobs might be fine, while a queue with 20 old jobs might mean the worker is stuck. Lag measures how long real work has waited, so it is a better operator signal for saturation.

## Validation

ChokaQ validates configuration during service registration. Invalid values fail application startup before workers can claim real jobs.

Examples of rejected values:

- zero or negative execution timeout.
- zero retry attempts.
- retry max delay smaller than retry base delay.
- heartbeat max interval smaller than heartbeat min interval.
- zero in-memory capacity.
- empty SQL connection string.
- zero SQL polling interval.
- zero SQL command timeout.
- zero SQL cleanup batch size.
- zero or negative worker heartbeat timeout.
- queue lag unhealthy threshold smaller than the degraded threshold.
- zero metric tag cardinality budget.
- blank metric unknown or overflow tag value.

Fail-fast validation is deliberate. A queue processor should not discover invalid operational policy after it has already accepted production work.
