# SQL Query Reference

![Fetch next batch query flow](/diagrams/04-fetch-next-batch-query.png)

This page explains the runtime intent behind the important SQL operations.
It is not a replacement for `Queries.cs`; it is the architectural reading guide
for the query set.

## Fetch Next Batch

Caller: `SqlJobStorage.FetchNextBatchAsync` from `SqlJobWorker.FetcherLoopAsync`.

Intent: claim eligible pending rows from `JobsHot` without blocking other
workers and without exceeding per-queue worker budgets.

Tables touched:

- `JobsHot`
- `Queues`

Key behavior:

- `ActiveCounts` counts fetched/processing rows per queue.
- `Candidates` finds pending rows that are due and not paused.
- `ROW_NUMBER()` ranks candidates inside each queue.
- `UPDLOCK, READPAST` lets workers skip rows already being claimed.
- `UPDATE ... OUTPUT inserted.*` claims rows and returns the claimed data.

Failure mode: if no rows are eligible, the worker sleeps for `PollingInterval`.
If the SQL call fails transiently, the storage retry policy can retry the
operation.

## Mark As Processing

Caller: `JobProcessor` through `IJobStateManager`.

Intent: convert a fetched row into an execution lease right before user code
runs.

Tables touched:

- `JobsHot`

Key behavior:

- Sets `Status = Processing`.
- Increments `AttemptCount`.
- Sets `StartedAtUtc`.
- Initializes `HeartbeatUtc`.
- Requires matching `WorkerId` and `Fetched` status when worker ownership is
  supplied.

This is the final stale-prefetch guard. If the row was released, reclaimed, or
changed after fetch, the update affects zero rows and user code is not executed.

## Keep Alive

Caller: job heartbeat loop during processing.

Intent: update `HeartbeatUtc` so zombie rescue knows the job is still alive.

Tables touched:

- `JobsHot`

Failure mode: repeated heartbeat write failures are logged and counted. Based
on configuration, execution may continue or be cancelled after a threshold.

## Archive Succeeded

Caller: `JobProcessor` after successful handler completion.

Intent: atomically move a completed job from active work to history.

Tables touched:

- `JobsHot`
- `JobsArchive`
- `StatsSummary`
- `MetricBuckets`

Key behavior:

- Starts a short transaction with `XACT_ABORT ON`.
- Deletes the exact row from `JobsHot`.
- Captures deleted data into a table variable via `OUTPUT`.
- Inserts captured data into `JobsArchive`.
- Updates lifetime counters.
- Updates rolling metric buckets.
- Commits only if the whole move succeeds.

## Reschedule For Retry

Caller: `JobStateManager` after retryable failure or open circuit.

![Retry and DLQ query flow](/diagrams/05-retry-dlq-query-flow.png)

Intent: keep the job in `JobsHot`, set it back to pending, and schedule it for
a future retry.

Tables touched:

- `JobsHot`
- `StatsSummary`

Key behavior:

- Status becomes pending.
- Worker ownership is cleared.
- `ScheduledAtUtc` is set to the calculated retry time.
- Retry counters are updated for operator visibility.

## Move To DLQ

Caller: failure finalization, cancellation, zombie rescue, and admin paths.

Intent: atomically remove a job from active execution and preserve it for
operator inspection.

Tables touched:

- `JobsHot`
- `JobsDLQ`
- `StatsSummary`
- `MetricBuckets`

The pattern mirrors Archive: delete from Hot, capture deleted row, insert into
DLQ, update counters and metric buckets in the same transaction.

## Resurrect

Caller: The Deck or storage API.

Intent: atomically move a DLQ job back into active work.

Tables touched:

- `JobsDLQ`
- `JobsHot`
- `StatsSummary`

Key behavior:

- Deletes from `JobsDLQ` first.
- Inserts into `JobsHot` with status pending and attempt count reset.
- Optionally applies edited payload/tags/priority.
- Rolls back the DLQ delete if the Hot insert fails.

## Recover Abandoned

Caller: `ZombieRescueService`.

Intent: release fetched rows that never reached processing.

Tables touched:

- `JobsHot`

Fetched rows have not executed user code. If they sit too long, the system can
safely reset them to pending by clearing `WorkerId` and setting `Status = 0`.

## Archive Zombies

Caller: `ZombieRescueService`.

![Zombie rescue query flow](/diagrams/06-zombie-rescue-query-flow.png)

Intent: move processing rows with expired heartbeat to DLQ.

Tables touched:

- `JobsHot`
- `JobsDLQ`
- `StatsSummary`
- `MetricBuckets`
- `Queues`

Processing zombies are not automatically retried because user code may have
already produced side effects.

## Dashboard Reads

Dashboard queries use bounded, purpose-specific reads:

| Query | Purpose |
|---|---|
| `GetSummaryStats` | Global lifetime counts. |
| `GetQueueStats` | Per-queue status counts and totals. |
| `GetQueueHealth` | Queue lag and pending saturation. |
| `GetThroughputStats` | Recent rate from `MetricBuckets`. |
| `GetTopDlqErrors` | Bounded recent DLQ error grouping. |
| `GetArchivePaged` | Committed archive page. |
| `GetDLQPaged` | Committed DLQ page. |

Telemetry reads may use approximate committed snapshots where exactness is not
part of the safety contract. State transitions do not rely on those snapshots.

## Architecture Decision

### Why this pattern?

The database is the coordination boundary. Workers do not coordinate through
process memory or distributed locks; they claim rows through SQL state changes.

### Trade-offs

SQL coordination is transparent and durable, but the database becomes a central
performance boundary. Index design, short transactions, and bounded dashboard
queries are therefore part of the runtime contract.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Distributed lock service | Explicit lease control. | Extra infrastructure and another failure mode. |
| Broker visibility timeout | Proven queue primitive. | Less direct SQL inspection and different operational model. |
| App-level copy/delete | Easy to write. | Unsafe cross-table moves under crash/failure. |

### Additional Questions

**Why use `UPDATE ... OUTPUT` for fetch?**  
Because claiming and returning the exact claimed rows must be one operation.

**What protects against stale prefetched work?**  
`MarkAsProcessing` validates worker ownership immediately before dispatch.

**Are dashboard `NOLOCK` reads used for correctness?**  
No. They are for observational views only. Correctness comes from state
transition predicates and transactions.
