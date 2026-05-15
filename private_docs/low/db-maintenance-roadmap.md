# DB Maintenance And Safe Purge Roadmap

This file tracks the roadmap for turning ChokaQ cleanup and purge operations
from safe batched deletes into a full SRE-grade database maintenance subsystem.
It is intentionally not linked from the public docs navigation.

## Current State

ChokaQ already has a production-preview cleanup baseline:

- `PurgeArchiveAsync` deletes Archive rows in repeated bounded SQL batches.
- `PurgeDLQAsync` deletes selected DLQ IDs in repeated bounded SQL batches.
- SQL batch size is controlled by `SqlServer.CleanupBatchSize`.
- Storage APIs accept `CancellationToken`.
- DLQ filtered bulk operations support preview, caps, typed confirmation, and
  destructive authorization.
- The Deck has simple Settings cleanup buttons for Archive and DLQ retention.
- The Deck has a stronger DLQ bulk workflow in history filters with preview,
  matched count, affected count, sample IDs, cap, and confirmation.
- Integration tests cover multi-batch Archive cleanup, multi-batch selected DLQ
  purge, filtered DLQ preview, filtered DLQ purge, and filtered DLQ requeue.

The current implementation is not yet a full maintenance engine:

- No `PurgeOptions` contract.
- No public throttling delay between batches.
- No progress DTO/events.
- No ETA or progress bar.
- No step mode.
- No max runtime guard.
- No purge-specific OpenTelemetry metrics.
- No temp-table or `SqlBulkCopy` path for very large selected-ID DLQ purges.
- No low-priority maintenance connection profile.
- No adaptive throttle.
- No dedicated DB Maintenance screen.
- No counter audit or repair command for materialized `StatsSummary` drift.
- No UI action that compares stored lifetime counters against table-derived
  counts before correcting them.

## Design Principles

- Maintenance work must never compromise active job processing.
- Large deletes must be many small transactions, not one large transaction.
- Operator workflows must show blast radius before destructive mutation.
- Every destructive operation must be cancellable.
- Progress and final outcome must be observable through UI, logs, and metrics.
- Archive cleanup and DLQ cleanup should share one execution model.
- The public storage API should remain simple for common use, with advanced
  options available through explicit maintenance APIs.
- The first production version should prefer predictable throttling over clever
  automation. Adaptive behavior can be introduced after static controls are
  measurable.
- Counter repair must be preview-first. Operators should see current values,
  recomputed values, and deltas before any write occurs.
- Counter repair must be honest about what can and cannot be reconstructed.
  `SucceededTotal` and `FailedTotal` can be derived from Archive/DLQ rows, while
  `RetriedTotal` and rolling `MetricBuckets` need event history for exact
  rebuilds.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until the maintenance engine is stable. |

## Executive Sequence

1. Formalize the maintenance execution contract.
2. Add a controlled purge engine with batch delay, cancellation, progress, and
   max runtime.
3. Route Archive cleanup and selected/filtered DLQ purge through the engine.
4. Add purge-specific logs and metrics.
5. Build a DB Maintenance screen with preview, execution, progress, and
   confirmation.
6. Optimize large selected-ID DLQ purge with a temp-table path.
7. Add low-priority workload controls.
8. Add adaptive throttling and future automation hooks.
9. Add counter audit/repair as a maintenance recovery action, not as part of
   normal dashboard refresh.

## Phase 0: Safe Core

Goal: preserve and harden the existing batched-delete baseline.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 0.1 | P0 | Done | Batch Archive purge. | `PurgeArchiveAsync` loops over bounded `TOP (@BatchSize)` deletes until the cutoff is clean. |
| 0.2 | P0 | Done | Batch selected DLQ purge. | `PurgeDLQAsync` deletes selected IDs across multiple short SQL transactions. |
| 0.3 | P0 | Done | Configurable cleanup batch size. | `SqlServer.CleanupBatchSize` controls rows deleted per cleanup transaction. |
| 0.4 | P0 | Done | Keep API backward compatible. | Existing purge methods still work as single operator actions. |
| 0.5 | P1 | Done | Update counters by actual deleted rows. | Queue summary counters decrement only by rows committed in the transaction. |
| 0.6 | P1 | Done | Add multi-batch tests. | Integration tests prove Archive and selected DLQ purge cross batch boundaries correctly. |
| 0.7 | P1 | Done | Add high-volume guardrail test. | A SQL integration test seeds a larger archive dataset and proves cleanup remains bounded by batch count and command budget. |

## Phase 1: Controlled Execution Engine

Goal: turn purge into a managed operation instead of a hidden loop inside
storage methods.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 1.1 | P0 | Open | Add `PurgeOptions`. | Options include `BatchSize`, `Delay`, `MaxDuration`, and `StepMode`. |
| 1.2 | P0 | Partial | Propagate cancellation. | Every batch boundary observes `CancellationToken`; public APIs already accept tokens, but UI execution must expose cancellation. |
| 1.3 | P1 | Open | Add static throttling delay. | Engine can wait between batches with `Task.Delay(delay, ct)`. |
| 1.4 | P1 | Open | Add progress DTO. | Progress reports include operation ID, target, deleted rows, batches completed, elapsed time, and current rate. |
| 1.5 | P1 | Open | Add step mode. | Advanced mode executes one batch per operator action for cautious production work. |
| 1.6 | P1 | Open | Add max runtime guard. | Engine stops cleanly when elapsed time exceeds configured `MaxDuration`. |
| 1.7 | P1 | Open | Return execution result. | Final result distinguishes completed, canceled, timed out, failed, and no-op outcomes. |
| 1.8 | P2 | Open | Keep legacy methods as wrappers. | Existing `PurgeArchiveAsync` and `PurgeDLQAsync` call the engine with safe defaults. |

Suggested contracts:

```csharp
public sealed record PurgeOptions(
    int BatchSize = 1000,
    TimeSpan? Delay = null,
    TimeSpan? MaxDuration = null,
    bool StepMode = false);

public sealed record PurgeProgress(
    string OperationId,
    PurgeTarget Target,
    long DeletedRows,
    int BatchesCompleted,
    TimeSpan Elapsed,
    double RowsPerSecond);

public sealed record PurgeResult(
    string OperationId,
    PurgeTarget Target,
    PurgeOutcome Outcome,
    long DeletedRows,
    int BatchesCompleted,
    TimeSpan Elapsed);
```

## Phase 2: Preview And Estimation

Goal: make maintenance blast radius visible before execution.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 2.1 | P0 | Partial | DLQ filtered preview. | Existing DLQ bulk preview returns matched count, affected count, and sample IDs. |
| 2.2 | P1 | Open | Archive cleanup preview. | Preview returns estimated Archive rows for cutoff and estimated batch count. |
| 2.3 | P1 | Open | DLQ retention preview. | Preview returns estimated DLQ rows older than cutoff and estimated batch count. |
| 2.4 | P1 | Open | Selected-ID purge estimate. | Preview can estimate selected DLQ IDs without executing deletion. |
| 2.5 | P2 | Open | Cost hint fields. | Preview can include recommended batch size and warning level. |
| 2.6 | P2 | Open | Count query budgets. | Preview queries have explicit command-timeout and performance tests. |

Preview result shape:

```csharp
public sealed record MaintenancePreview(
    PurgeTarget Target,
    long EstimatedRows,
    int EstimatedBatches,
    int RecommendedBatchSize,
    MaintenanceRiskLevel RiskLevel,
    IReadOnlyList<string> SampleIds);
```

## Phase 3: DB Maintenance UI

Goal: replace simple purge buttons with an operational control panel.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 3.1 | P1 | Open | Add DB Maintenance screen. | The Deck has a dedicated maintenance surface separate from general Settings. |
| 3.2 | P1 | Open | Archive cleanup panel. | Operator can set cutoff date, batch size, delay, max runtime, and execution mode. |
| 3.3 | P1 | Open | DLQ cleanup panel. | Operator can choose selected IDs, filter-based subset, retention cutoff, or explicit all-with-warning mode. |
| 3.4 | P1 | Open | Preview before execution. | UI shows estimated rows and estimated batches before enabling destructive execution. |
| 3.5 | P1 | Open | Progress view. | UI shows progress bar, rows deleted, batches completed, elapsed time, rate, and approximate ETA. |
| 3.6 | P1 | Open | Cancellation button. | Operator can cancel an active maintenance run safely at a batch boundary. |
| 3.7 | P2 | Open | Step mode UI. | Advanced mode can run exactly one batch per click. |
| 3.8 | P2 | Open | Operation history. | Last maintenance runs show target, actor, result, row count, duration, and failure details. |
| 3.9 | P2 | Open | Add counter audit action. | Maintenance UI can run a read-only counter verification and show deltas. |
| 3.10 | P2 | Open | Add guarded counter repair action. | Repair is enabled only after preview and requires destructive/admin authorization. |

Current UI inventory:

- Settings has simple Archive and DLQ retention buttons.
- DLQ History has filtered bulk preview and typed confirmation.
- There is no unified maintenance control panel yet.

## Phase 4: Large DLQ Selected-ID Optimization

Goal: remove JSON parsing as the bottleneck for very large selected-ID DLQ
operations.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 4.1 | P1 | Open | Add temp-table selected-ID path. | SQL Server path creates `#TempIds` and deletes through an indexed join. |
| 4.2 | P1 | Open | Bulk insert IDs. | C# writes selected IDs with `SqlBulkCopy` or equivalent table-valued strategy. |
| 4.3 | P1 | Open | Batch delete through temp table. | Deletion still uses bounded `TOP (@BatchSize)` batches. |
| 4.4 | P1 | Open | Preserve stats correctness. | Queue counters decrement by rows actually deleted. |
| 4.5 | P1 | Open | Add 100k-ID integration test. | Test proves large selected-ID purge is stable and avoids repeated JSON parsing. |
| 4.6 | P2 | Open | Retain JSON path for small batches. | Small selected purges can keep the simpler existing JSON path. |

Implementation note:

```sql
CREATE TABLE #TempIds ([Id] varchar(50) NOT NULL PRIMARY KEY);

;WITH Picked AS (
    SELECT TOP (@BatchSize) d.[Id]
    FROM [chokaq].[JobsDLQ] d WITH (UPDLOCK, READPAST)
    INNER JOIN #TempIds t ON t.[Id] = d.[Id]
    ORDER BY d.[FailedAtUtc] ASC, d.[Id] ASC
)
DELETE d
OUTPUT deleted.[Queue]
FROM [chokaq].[JobsDLQ] d
INNER JOIN Picked p ON p.[Id] = d.[Id];
```

## Phase 5: Low-Priority Maintenance Workload

Goal: reduce the impact of maintenance work on active workers and dashboard
latency.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 5.1 | P1 | Open | Add optional maintenance connection string. | Hosts can route maintenance to a separate SQL login, pool, or workload group. |
| 5.2 | P1 | Open | Add maintenance command timeout. | Maintenance timeout can differ from worker storage command timeout. |
| 5.3 | P1 | Open | Add `MAXDOP 1` option. | Maintenance deletes can run with low parallelism where SQL Server supports the hint. |
| 5.4 | P1 | Open | Add delay defaults. | Default maintenance delay avoids IO/CPU spikes on shared SQL Server. |
| 5.5 | P2 | Open | Add lock-wait friendliness. | Maintenance can back off when blocked instead of competing aggressively. |
| 5.6 | P2 | Open | Document SQL workload guidance. | Docs explain connection pool, SQL login, Resource Governor, and shared-database tradeoffs. |

## Phase 6: Observability

Goal: make purge behavior measurable and auditable.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 6.1 | P1 | Open | Add purge row counter. | Metric records total rows deleted by target and result. |
| 6.2 | P1 | Open | Add purge batch counter. | Metric records batches completed by target and result. |
| 6.3 | P1 | Open | Add purge duration histogram. | Metric records operation duration in seconds. |
| 6.4 | P1 | Open | Add purge rate observable field. | UI and logs report rows per second; metric can be derived from rows/duration. |
| 6.5 | P1 | Open | Add structured log events. | Start, batch progress, cancellation, timeout, completion, and failure use stable `EventId` values. |
| 6.6 | P1 | Open | Add SignalR progress events. | The Deck receives progress updates for active maintenance operations. |
| 6.7 | P2 | Open | Add audit trail storage. | Optional maintenance history records actor, target, options, result, and error details. |

Suggested metric names:

- `chokaq.maintenance.purge.rows_deleted`
- `chokaq.maintenance.purge.batches`
- `chokaq.maintenance.purge.duration`

Suggested log event range:

- `7000-7099`: maintenance lifecycle and purge operations.

## Phase 7: Safety And UX Guards

Goal: prevent accidental destructive operations and unsafe runtime pressure.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 7.1 | P1 | Partial | Typed confirmation. | Existing selected and filtered DLQ purge paths require typed confirmation; maintenance screen must apply the same rule. |
| 7.2 | P1 | Open | Double confirmation for DLQ all. | All-DLQ purge requires an additional explicit confirmation and warning copy. |
| 7.3 | P1 | Open | Large-operation warning. | UI flags high estimated row counts, for example 1M+ rows. |
| 7.4 | P1 | Open | Enforce max batch size. | API validates maximum batch size and rejects unsafe values. |
| 7.5 | P1 | Open | Enforce max runtime. | UI and engine stop at configured runtime cap. |
| 7.6 | P2 | Open | Recommended defaults. | Preview recommends batch size and delay based on estimated row count. |
| 7.7 | P2 | Open | Require destructive policy. | All maintenance execution paths require destructive dashboard authorization. |
| 7.8 | P2 | Open | Prevent concurrent conflicting runs. | Same target cannot run overlapping purge operations unless explicitly allowed. |

## Phase 8: Adaptive Throttling

Goal: make maintenance automatically polite under production load.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 8.1 | P2 | Deferred | Define database pressure signal. | Pressure can be derived from command latency, lock wait, worker queue lag, or explicit provider. |
| 8.2 | P2 | Deferred | Add adaptive delay policy. | Delay increases under pressure and decreases when the database is healthy. |
| 8.3 | P2 | Deferred | Add policy options. | Hosts can choose static, adaptive, or disabled throttling. |
| 8.4 | P2 | Deferred | Add simulation tests. | Tests prove delay increases/decreases based on injected pressure signals. |
| 8.5 | P3 | Deferred | Add operator visibility. | UI shows current throttle mode and reason for delay changes. |

Recommendation: implement static delay and max runtime before adaptive behavior.

## Phase 9: Future Automation Hooks

Goal: make manual maintenance reusable by future background automation without
adding cron behavior prematurely.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 9.1 | P2 | Deferred | Define retention policy model. | Model can describe Archive and DLQ retention without scheduling it yet. |
| 9.2 | P2 | Deferred | Reuse maintenance engine for scheduled cleanup. | Future auto purge calls the same engine and emits the same metrics/events. |
| 9.3 | P3 | Deferred | Add cold-storage export hook. | Export-before-delete can be plugged in before rows are purged. |
| 9.4 | P3 | Deferred | Add dry-run scheduled reports. | Operators can preview what automation would delete before enabling it. |

## Phase 10: Materialized Counter Audit And Repair

Goal: give operators a safe recovery tool when materialized summary counters
drift from the canonical tables.

Why this belongs in maintenance:

- `StatsSummary` exists so dashboard counters can be O(1) reads.
- Archive and DLQ tables remain the canonical historical sources for succeeded
  and failed job counts.
- Rare drift can happen through manual database edits, failed experimental
  migrations, storage bugs, or partially restored backups.
- Rebuilding counters scans history tables, so it should not run as part of
  normal dashboard refresh.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 10.1 | P1 | Open | Add counter audit query. | Storage can compute Archive count and DLQ count per queue and compare them with `StatsSummary`. |
| 10.2 | P1 | Open | Add counter audit DTO. | Result includes queue, stored succeeded/failed, computed succeeded/failed, deltas, scan timestamp, and warning level. |
| 10.3 | P1 | Open | Add dry-run UI action. | "Verify counters" runs read-only and never updates `StatsSummary`. |
| 10.4 | P1 | Open | Add guarded repair command. | "Repair counters" updates `SucceededTotal` and `FailedTotal` from computed values only after preview and confirmation. |
| 10.5 | P1 | Open | Preserve `RetriedTotal` explicitly. | Repair does not overwrite `RetriedTotal` unless an exact event source exists. UI explains the limitation. |
| 10.6 | P2 | Open | Add scoped repair option. | Operator can repair all queues or one selected queue. |
| 10.7 | P2 | Open | Add audit log entry. | Repair records actor, queue scope, before values, after values, deltas, and duration. |
| 10.8 | P2 | Open | Run under maintenance workload controls. | Audit and repair use command timeout, optional delay/low-priority connection, and no overlapping conflicting maintenance run. |
| 10.9 | P2 | Open | Add repair notification. | The Deck refreshes counters after repair completes and records an operator log message. |
| 10.10 | P2 | Deferred | Rebuild rolling buckets. | `MetricBuckets` rebuild is only considered if future event history makes exact reconstruction possible. |

Candidate preview shape:

```csharp
public sealed record CounterAuditResult(
    string? Queue,
    long StoredSucceeded,
    long ComputedSucceeded,
    long StoredFailed,
    long ComputedFailed,
    long StoredRetried,
    long? ComputedRetried,
    DateTime ObservedAtUtc);
```

Candidate SQL direction:

```sql
SELECT [Queue], COUNT_BIG(1) AS ComputedSucceeded
FROM [chokaq].[JobsArchive]
GROUP BY [Queue];

SELECT [Queue], COUNT_BIG(1) AS ComputedFailed
FROM [chokaq].[JobsDLQ]
GROUP BY [Queue];
```

Important limitation:

`RetriedTotal` is a lifetime count of retry scheduling decisions. It is not the
same as `SUM(AttemptCount - 1)` across current rows, because jobs can be purged,
resurrected, still active, or restored from partial history. Preserve it unless
ChokaQ later stores a durable retry event stream.

## Suggested Implementation Order

1. Add `PurgeOptions`, `PurgeProgress`, `PurgeResult`, and `PurgeOutcome`.
2. Introduce an internal maintenance executor in SQL storage.
3. Move `PurgeArchiveAsync` to the executor.
4. Move selected `PurgeDLQAsync` to the executor while preserving existing API.
5. Add Archive and DLQ retention preview APIs.
6. Add metrics and structured log events.
7. Build the DB Maintenance UI around preview, execute, cancel, and progress.
8. Add temp-table optimization for large selected-ID DLQ purges.
9. Add low-priority workload controls.
10. Add counter audit/repair after the maintenance screen has preview and
    confirmation patterns.
11. Defer adaptive throttling and automation until static controls are proven.

## Test Plan

Required unit tests:

- `PurgeOptions` validation rejects unsafe values.
- Engine stops after cancellation at a batch boundary.
- Engine stops after `MaxDuration`.
- Progress reports rows, batches, elapsed time, and rate.
- Step mode executes one batch and returns a partial result.
- Metrics record rows, batches, and duration.
- Large-operation warnings map to expected risk levels.

Required integration tests:

- Archive cleanup deletes across many batches and preserves active jobs.
- DLQ selected purge deletes across many batches and updates counters.
- Filtered DLQ purge remains bounded by `MaxJobs`.
- Temp-table selected purge handles 100k IDs without JSON parsing.
- Cancellation leaves already committed batches deleted and unprocessed rows intact.
- Low-delay and high-delay options produce expected elapsed-time behavior within a
  tolerance.
- Maintenance commands use read-committed paths for previews and mutation paths.
- Counter audit detects seeded drift in `StatsSummary`.
- Counter repair updates succeeded/failed totals from Archive/DLQ counts and
  preserves `RetriedTotal`.

Required UI tests or component tests:

- DB Maintenance screen requires preview before execute.
- Large row count shows warning.
- DLQ all requires double confirmation.
- Cancel button disables after cancellation starts.
- Progress updates are rendered.
- Completion result shows rows, batches, duration, and outcome.
- Destructive authorization blocks execution but permits read-only preview where
  policy allows.
- Counter verification shows before/after deltas without mutating data.
- Counter repair requires confirmation and refreshes the stats display after
  completion.

## Open Design Questions

- Should maintenance operations live in `IJobStorage`, a new
  `IJobMaintenanceService`, or a SQL-specific maintenance service?
- Should preview be allowed for read-only dashboard users while execution
  requires destructive policy?
- Should selected-ID DLQ purge return `PurgeResult` in a new API while legacy
  `PurgeDLQAsync` remains `ValueTask`?
- Should the temp-table path activate by ID count threshold, payload size, or
  explicit option?
- Should maintenance operation history be stored in SQL or only emitted as logs
  and metrics for the preview line?
- Should adaptive throttling use internal SQL latency only, or allow host apps to
  inject their own pressure signal?
- Should counter repair live in SQL-specific maintenance APIs only, or should
  `IJobStorage` expose a provider-neutral repair contract?
- Should `RetriedTotal` remain manual-only until an event stream exists, or
  should ChokaQ expose an approximate recomputation option with explicit labels?

## Non-Goals For The First Maintenance Release

- Automatic scheduled purge.
- Retention policies that mutate data without an operator.
- Cross-database cold storage.
- Full SQL Server Resource Governor integration.
- Machine-learning or predictive throttling.
- Provider-independent maintenance abstraction beyond the current SQL Server
  implementation.
