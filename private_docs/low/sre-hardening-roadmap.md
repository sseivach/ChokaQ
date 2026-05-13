# SRE Hardening Roadmap

This file tracks the remaining SRE-grade hardening work for ChokaQ. It is
intentionally private and not linked from the public documentation navigation.

The goal is not to add features for their own sake. The goal is to reduce
operational risk: faster detection, faster triage, safer recovery, stronger data
integrity, and clearer production behavior under stress.

Priority: low for the first NuGet preview.

Reason: the existing production-preview baseline is enough to publish and learn
whether people try the package. Full SLO, alerting, runbook, audit timeline, and
OpenTelemetry work should follow after real usage validates the direction.

## Current State

ChokaQ already has a strong production-preview operational baseline:

- Queue lag is treated as the primary saturation signal.
- Queue lag is exposed through metrics, health checks, and The Deck.
- SQL queue-lag reads use indexed Pending hot rows instead of full history scans.
- Throughput and failure-rate windows are materialized through `MetricBuckets`.
- Top DLQ error families are bounded and clickable from the dashboard.
- Failure taxonomy separates transient, fatal, throttled, timeout, canceled,
  max-retry, and zombie-style failures.
- Worker finalization uses storage-level ownership guards so only the current
  lease holder can archive, DLQ, or retry a job.
- The Deck separates read access from destructive operations.
- DLQ bulk operations have preview, caps, typed confirmation, and actor-aware
  mutation paths for the main operator flows.

The remaining gap is the full SRE loop:

1. Define service-level objectives.
2. Turn objective breaches into alerts.
3. Route alerts to concrete operator actions.
4. Record recovery actions with enough audit detail to reconstruct incidents.
5. Verify these behaviors under realistic load, lease expiration, and incident
   recovery scenarios.
6. Add tracing guidance so operators can connect enqueue, fetch, dispatch,
   handler execution, retry, DLQ, and archive transitions during incidents.

## Design Principles

- Reliability work should be framed around risk reduction, not feature count.
- The primary page-worthy signal is queue lag, not queue depth.
- Failure visibility should reduce MTTR by making triage decisions obvious.
- Ownership must be enforced at the storage layer, not only by worker memory.
- Bulk actions are incident-recovery tools and therefore need blast-radius
  controls, rate limits, and auditability.
- Alerting must close the loop from detection to action; metrics without
  runbooks are only dashboards.
- Expensive observability queries must use indexed hot-path rows, bounded
  windows, materialized buckets, or sampling.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until the core SRE loop is complete. |

## Executive Sequence

1. Formalize ChokaQ SLOs and operational budgets.
2. Convert existing health signals into alert rules with recommended actions.
3. Add incident runbooks for queue lag, DLQ spikes, throttling, fatal failures,
   worker ownership conflicts, and bulk recovery.
4. Harden bulk operations with rate limiting and persistent audit events.
5. Add targeted stress and regression tests for observability cost, lease
   expiration, and incident recovery workflows.
6. Document how operators should scale workers, investigate dependencies,
   pause queues, requeue DLQ jobs, and stop destructive actions.
7. Add OpenTelemetry tracing only after the core metric and runbook loop is
   stable enough to define useful span names and attributes.

## Phase A: Observability Cost And Signal Quality

Goal: keep the dashboard useful at production scale without turning monitoring
into a new source of database load.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Treat queue lag as the primary saturation signal. | README, docs, health checks, and The Deck all expose queue lag as the first capacity signal. |
| A.2 | P0 | Done | Avoid history-table scans for rate windows. | Recent throughput and failure rate read from `MetricBuckets`, not large Archive/DLQ scans. |
| A.3 | P0 | Done | Use indexed Pending rows for lag reads. | SQL health queries calculate Avg/Max Pending lag through hot-path indexed access paths. |
| A.4 | P1 | Done | Bound top-error queries. | Top DLQ error families are grouped over a bounded window and exposed as dashboard triage links. |
| A.5 | P1 | Open | Add explicit query-budget tests. | SQL performance tests assert queue-lag and top-error queries stay bounded on large datasets. |
| A.6 | P1 | Open | Add sampling/sliding-window policy docs. | Docs explain when to use indexed Pending rows, bounded windows, or sampling for high-cardinality observability. |
| A.7 | P2 | Open | Add dashboard stale-data indicators. | The Deck shows when health data is stale or refresh failed so operators do not trust old signals. |
| A.8 | P2 | Open | Add OpenTelemetry tracing plan. | Docs define span boundaries and attributes for enqueue, fetch, dispatch, handler execution, retry, DLQ, archive, and purge operations. |
| A.9 | P2 | Open | Add trace correlation IDs. | Job ID, queue, type key, attempt, worker ID, operation ID, and idempotency key are available as trace attributes where safe. |

Operational framing:

> We avoid full scans by computing lag only on indexed Pending jobs and by using
> materialized or bounded windows for rate and error-family reads.

## Phase B: Failure Transparency And MTTR Reduction

Goal: make failure classes actionable so operators can distinguish capacity,
dependency, payload, and code problems quickly.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P0 | Done | Add failure taxonomy. | DLQ rows carry structured failure reasons such as `Transient`, `Fatal`, `Throttled`, `Timeout`, `Cancelled`, `MaxRetriesExceeded`, and `Zombie`. |
| B.2 | P0 | Done | Surface taxonomy in The Deck. | Operators can filter and inspect jobs by failure reason. |
| B.3 | P1 | Done | Add top-error click-through. | Selecting a top error applies a matching DLQ investigation filter. |
| B.4 | P1 | Partial | Frame taxonomy as triage, not badges. | Existing UI exposes the data; public docs should state how it reduces MTTR and unnecessary intervention. |
| B.5 | P1 | Open | Add failure-class runbook mapping. | Each failure reason maps to recommended action, owner, urgency, and requeue safety guidance. |
| B.6 | P2 | Open | Add dependency correlation fields. | Optional metadata can identify external dependency, endpoint, tenant, or subsystem for faster incident slicing. |

Target framing:

> Operators can immediately distinguish throttling from fatal payload or code
> errors, reducing unnecessary interventions and speeding up triage.

## Phase C: Worker Ownership And Data Integrity

Goal: prevent stale workers from finalizing jobs after their lease was lost,
even during GC pauses, process stalls, restarts, or network partitions.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P0 | Done | Enforce finalization ownership in SQL. | Archive, DLQ, and retry transitions include `WorkerId` guards. |
| C.2 | P0 | Done | Guard processing claims. | `MarkAsProcessing` succeeds only when the row is still owned by the expected worker/status state. |
| C.3 | P0 | Done | Return stale-finalization results. | Storage methods return false when a worker no longer owns the row, allowing the worker to stop touching it. |
| C.4 | P1 | Done | Separate fetched timeout from execution timeout. | Prefetched jobs are not reclaimed as zombies while they are only buffered. |
| C.5 | P1 | Open | Add lease-expiration regression tests. | Tests simulate a stale worker finalizing after another worker reclaimed the job and prove only the current owner can mutate it. |
| C.6 | P1 | Open | Add ownership-conflict telemetry. | Failed ownership-guard transitions emit a structured metric/log so lease pressure can be detected. |
| C.7 | P2 | Open | Add worker clock-skew guidance. | Docs explain timeout settings, heartbeat intervals, and clock assumptions for multi-node deployments. |

Target framing:

> We enforce ownership at the storage level so that only the current lease holder
> can finalize a job. This prevents double finalization even under GC pauses,
> process stalls, or network partitions.

## Phase D: Bulk Operations As Incident Recovery

Goal: make bulk cancel, requeue, and purge safe enough for incident recovery
without making destructive operations too easy to misuse.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P0 | Done | Gate destructive commands. | The Deck can require a separate destructive authorization policy. |
| D.2 | P0 | Done | Add previews for filtered DLQ operations. | Operators see matched count, affected count, caps, and sample IDs before execution. |
| D.3 | P0 | Done | Add typed confirmations. | Selected and filtered destructive operations require an explicit confirmation step. |
| D.4 | P1 | Done | Add hard bulk caps. | Filtered DLQ bulk operations clamp requested limits to a configured absolute maximum. |
| D.5 | P1 | Partial | Preserve actor context. | Main edit, cancel, requeue, and filtered operations carry actor identity; selected purge and maintenance-style cleanup should be made fully consistent. |
| D.6 | P1 | Open | Add rate limiting for bulk actions. | The Deck rejects or delays repeated bulk mutations per actor/session to prevent accidental rapid-fire operations. |
| D.7 | P1 | Open | Add persistent bulk audit events. | Bulk cancel, requeue, purge, and maintenance cleanup write durable operation records with actor, filters, counts, timestamps, and result. |
| D.8 | P2 | Open | Add bulk operation IDs. | UI, logs, audit records, and metrics share a stable operation ID for incident reconstruction. |
| D.9 | P2 | Open | Add undo-aware guidance where possible. | Requeue/cancel docs distinguish reversible recovery actions from permanent purge. |

Operational framing:

> Bulk actions are not only convenience features. They are recovery controls
> used during incidents, so they need blast-radius limits, clear previews,
> auditability, and rate limits.

## Phase E: SLO, Alert, And Action Loop

Goal: close the loop between detection and production response.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P0 | Open | Define default ChokaQ SLOs. | Docs define default objectives for queue lag, DLQ rate, worker liveness, and storage health. |
| E.2 | P0 | Open | Add alert matrix. | Each SLO breach maps to signal, severity, likely cause, and first action. |
| E.3 | P0 | Open | Add queue-lag runbook. | Runbook covers scaling workers, raising queue `MaxWorkers`, splitting queues, reducing producer rate, and identifying SQL bottlenecks. |
| E.4 | P0 | Open | Add DLQ-spike runbook. | Runbook distinguishes throttling, dependency failures, bad payloads, code defects, and retry-safe recovery. |
| E.5 | P1 | Open | Add worker-health runbook. | Runbook covers no active workers, zombie recovery, stale heartbeat, and ownership-conflict investigation. |
| E.6 | P1 | Open | Add bulk-recovery runbook. | Runbook explains when to use bulk requeue, filtered requeue, cancel, purge, and maintenance cleanup. |
| E.7 | P1 | Open | Add alert examples. | Provide Prometheus/OpenTelemetry-style example rules or pseudocode for the documented signals. |
| E.8 | P2 | Open | Add environment-specific tuning section. | Docs explain how to adapt thresholds for user-facing, batch, low-priority, and high-volume queues. |

Initial SLO candidates:

| Objective | Default Target | Alert Candidate |
|---|---:|---|
| Queue processing latency | 99% of eligible jobs start within 5 seconds | Max queue lag exceeds 10 seconds for 5 minutes |
| DLQ rate | Less than 0.1% failed over 5 minutes | Failure rate exceeds 1% or jumps 5x baseline |
| Worker liveness | At least one active worker per enabled queue | No active worker heartbeat for an enabled non-paused queue |
| Storage health | SQL storage check remains healthy | Connectivity or schema health check is unhealthy |

Example alert-action loop:

| Signal | Interpretation | First Action |
|---|---|---|
| Lag rising, failure rate low | Capacity shortage or slow workers | Add workers, increase queue `MaxWorkers`, or reduce producer rate. |
| Lag rising with SQL timeouts | Database is bottlenecking job movement | Inspect SQL waits, indexes, locks, and maintenance workload. |
| DLQ spike with `Throttled` | Downstream quota or rate limit | Avoid blind requeue; reduce concurrency or wait for quota recovery. |
| DLQ spike with `Fatal` | Payload or handler defect | Inspect payload/error family, fix code or data, then requeue targeted subset. |
| Worker heartbeat missing | Worker deployment or process failure | Restart workers and confirm queue lease recovery. |

## Phase F: Incident Reconstruction

Goal: make it possible to answer what happened, who acted, what changed, and
whether recovery worked.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Partial | Use stable lifecycle `EventId` values. | Logs already have stable event IDs; runbooks should reference them directly. |
| F.2 | P1 | Open | Add audit table or audit sink abstraction. | Operator actions can be retained outside mutable job rows. |
| F.3 | P1 | Open | Add audit coverage tests. | Tests prove destructive actions record actor, action, target scope, requested count, affected count, and outcome. |
| F.4 | P2 | Open | Add incident timeline view. | The Deck can show recent operator mutations and system events for a queue or job family. |
| F.5 | P2 | Open | Export audit trail. | Operators can export incident-relevant audit entries for postmortems. |

Minimum audit event shape:

```csharp
public sealed record OperatorActionAudit(
    string OperationId,
    string Actor,
    string Action,
    string Target,
    string? FilterJson,
    int RequestedCount,
    int AffectedCount,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Outcome,
    string? Error);
```

## Phase G: Verification Under Stress

Goal: prove the hardening claims with tests that match the risks.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P1 | Open | Add large-dataset observability tests. | Health queries remain bounded with large Archive/DLQ/history volume. |
| G.2 | P1 | Open | Add ownership-race tests. | Stale workers cannot archive, DLQ, or retry jobs after lease loss. |
| G.3 | P1 | Open | Add bulk-safety tests. | Rate limits, caps, confirmation, actor propagation, and audit records are covered. |
| G.4 | P2 | Open | Add alert-threshold tests. | Queue saturation health check behavior is verified at healthy, degraded, and unhealthy boundaries. |
| G.5 | P2 | Open | Add incident drill script. | A repeatable local scenario demonstrates lag spike, DLQ spike, alert interpretation, and recovery. |

## Suggested Implementation Order

1. Write the public SLO and runbook docs using existing signals.
2. Add alert examples and threshold tuning guidance.
3. Add persistent audit events for destructive and bulk operations.
4. Add bulk mutation rate limiting.
5. Add ownership-conflict telemetry.
6. Add targeted SQL and worker-race regression tests.
7. Add stale-data indicators in The Deck.
8. Add OpenTelemetry tracing guidance after metrics and runbooks are stable.
9. Add incident timeline/export only after audit events are durable.

## Non-Goals

- Do not build a full incident-management product inside The Deck.
- Do not add automatic remediation before manual runbooks are clear.
- Do not introduce complex anomaly detection before fixed SLO thresholds are
  documented and measurable.
- Do not make purge automation part of this roadmap; that belongs to the DB
  Maintenance roadmap.
- Do not optimize observability by weakening correctness of job lifecycle
  transitions.
