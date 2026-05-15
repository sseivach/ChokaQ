# SLOs And Alerts

This page explains the operational signals ChokaQ exposes, what they mean, and
how to turn them into useful alerts. It is written for two audiences:

- developers who need to understand what their background jobs are doing;
- operators who need to decide whether to scale, investigate, retry, or stop.

ChokaQ is a background job engine. Its health is not just "is the process up?"
A process can be alive while jobs are waiting too long, workers are stuck, SQL
is slow, or a downstream provider is rejecting calls. Good monitoring starts
with user-visible delay and failure rate.

## Terms

| Term | Meaning |
|---|---|
| Eligible job | A job whose scheduled time has arrived and can be fetched by workers. |
| Queue lag | How long eligible pending work has been waiting before execution. |
| DLQ | Dead Letter Queue. Failed, cancelled, zombie, or poison work that needs operator attention. |
| Worker heartbeat | Process-local signal that the background worker loop is alive. |
| Job heartbeat | Per-job signal that a `Processing` job is still alive. |
| Failure reason | Structured class such as `Fatal`, `Throttled`, `Timeout`, `Zombie`, or `Cancelled`. |
| Circuit | A per-job-type breaker that can block repeated failing execution attempts. |

## Default Objectives

These are starting points, not universal law. A user-facing notification queue
and an overnight report queue should not have the same objective.

| Objective | Default target | Why it matters |
|---|---:|---|
| Queue processing latency | 99% of eligible jobs start within 5 seconds | This is the closest signal to "users are waiting too long." |
| DLQ rate | Less than 0.1% of completed attempts over 5 minutes | A rising DLQ rate means work is being lost from the normal path. |
| Worker liveness | At least one healthy worker loop for active queues | Jobs cannot drain if workers are stopped or stuck. |
| SQL storage health | SQL health check remains healthy | SQL mode depends on storage for admission, fetch, finalization, and dashboard reads. |
| Queue saturation health | Queue lag stays below the unhealthy threshold | Saturated queues can be alive but too slow to meet service expectations. |

For low-priority batch workloads, a 5-second lag target may be too strict. For
customer-facing security, payment, or notification jobs, it may be too loose.
Treat these defaults as a preview baseline and tune per queue.

## Primary Signals

ChokaQ exposes three layers of signals:

1. Health checks for yes/no platform decisions.
2. Metrics for trends and alerting.
3. The Deck for human triage and recovery.

### Health Checks

SQL mode registers:

| Check | Meaning |
|---|---|
| `chokaq_sql` | SQL Server is reachable and ChokaQ storage can query expected objects. |
| `chokaq_worker` | The hosted worker loop is running and has a recent heartbeat. |
| `chokaq_queue_saturation` | Pending queue lag is below configured degraded/unhealthy thresholds. |

Use health checks for readiness and basic monitoring. Do not rely on health
checks alone for incident diagnosis; they intentionally summarize detail.

### Metrics

The `ChokaQ` meter exposes:

| Instrument | Use it for |
|---|---|
| `chokaq.jobs.enqueued` | Producer activity and queue arrival rate. |
| `chokaq.jobs.completed` | Successful throughput. |
| `chokaq.jobs.failed` | Handler failures before retry or DLQ outcome. |
| `chokaq.jobs.processing_duration` | Slow handlers and timeout risk. |
| `chokaq.jobs.queue_lag` | Primary saturation signal. |
| `chokaq.jobs.dlq` | Failed work by queue, job type, and reason. |
| `chokaq.jobs.retried` | Retry pressure and dependency instability. |
| `chokaq.workers.active` | Active processing pressure. |
| `chokaq.jobs.heartbeat_failures` | SQL/network/host pressure during running jobs. |
| `chokaq.jobs.state_transition_conflicts` | Stale ownership attempts and lease races. |
| `chokaq.idempotency.claims` | Accepted, duplicate, completed, or rejected idempotency claims. |
| `chokaq.circuits.events` | Circuit open, half-open, close, and rejection activity. |

Metric labels are cardinality-capped by `ChokaQ:Metrics`. This protects your
monitoring system from unbounded queue names, job type names, error strings, or
failure reasons. Overflow values are grouped into a stable bucket such as
`other`.

### The Deck

The Deck is the operator console. Use it to answer:

- Which queues are lagging?
- Which jobs are active?
- Which failure reasons dominate the DLQ?
- Which error families repeat?
- Are circuits open?
- Did a queue get paused or capped?
- Can a failed payload be repaired and requeued?
- What happened after a bulk action?

The Deck is SignalR-assisted and periodically reconciles from storage. That
means it is designed for operational visibility, not as the only source of
truth. SQL remains the durable state boundary.

## Alert Matrix

Start with these alerts and tune them after observing real workload behavior.

| Alert | Suggested trigger | Likely meaning | First action |
|---|---|---|---|
| Queue lag high | Max queue lag > 10s for 5m | Workers are not keeping up, a queue is paused/capped, SQL is slow, or handlers are slow. | Open The Deck, identify the queue, check worker count and SQL health. |
| Queue lag high with low failures | Lag rising, DLQ rate normal | Capacity shortage or slow normal work. | Add workers, raise queue `MaxWorkers`, split queues, or reduce producer rate. |
| Queue lag high with SQL unhealthy | Lag rising, `chokaq_sql` unhealthy | Storage is the bottleneck. | Inspect SQL connectivity, waits, locks, CPU, disk, and command timeouts before adding more workers. |
| DLQ rate high | DLQ rate > 1% for 5m | Work is leaving the normal path. | Inspect top error families and failure reasons before requeueing. |
| Fatal DLQ spike | `chokaq.jobs.dlq{reason="Fatal"}` jumps | Code defect, incompatible payload, bad data, or unsupported contract. | Stop blind retries; fix handler or payload; requeue a targeted subset only after repair. |
| Throttled DLQ spike | `reason="Throttled"` jumps | Downstream rate limit or quota exhaustion. | Reduce concurrency, lower queue `MaxWorkers`, wait for quota recovery, then requeue carefully. |
| Timeout DLQ spike | `reason="Timeout"` jumps | Handler exceeds execution timeout or dependency latency changed. | Check handler duration and dependency latency; raise timeout only if side effects are idempotent and long runtime is expected. |
| Worker unhealthy | Worker heartbeat stale | Process stopped, host overloaded, or worker loop blocked. | Check host logs, restart if needed, confirm abandoned `Fetched` jobs recover. |
| Heartbeat failures | `chokaq.jobs.heartbeat_failures` increasing | SQL writes, network, locks, or host resources are under pressure. | Follow the heartbeat pressure runbook; avoid immediate cancellation unless configured intentionally. |
| State transition conflicts | Conflicts spike | Stale worker tried to finalize work it no longer owns. | Check for slow handlers, zombie recovery, clock/timeout settings, and worker restarts. |
| Circuit open events | Circuit open/reject events rising | One job type is repeatedly failing. | Do not scale that job type blindly; inspect error family and downstream health. |

## Safe Alert Response

When an alert fires, avoid changing everything at once. Use this order:

1. Identify whether the problem is lag, failure, worker liveness, or SQL health.
2. Identify the affected queue and job type.
3. Check whether the failure reason is retry-safe.
4. Apply the smallest useful control: scale workers, pause a queue, lower
   queue `MaxWorkers`, repair payloads, or requeue a targeted subset.
5. Watch queue lag, DLQ rate, retries, and SQL health after the change.

The most common unsafe response is blind requeue. Blind requeue can multiply
side effects, hammer a downstream dependency, or hide a real payload defect.

## Recommended Dashboards

A useful first dashboard has these panels:

| Panel | Group by | Why |
|---|---|---|
| Queue lag p95/max | queue | Shows user-visible waiting time. |
| Enqueue rate | queue, type | Shows producer pressure. |
| Completed rate | queue, type | Shows processing throughput. |
| DLQ rate | queue, type, reason | Shows failed work by class. |
| Retry rate | queue, type | Shows instability before DLQ. |
| Processing duration p95/max | queue, type | Shows timeout risk and slow handlers. |
| Active workers | queue | Shows capacity usage. |
| Circuit events | type, state | Shows failing job families. |
| Health check state | check name | Shows platform readiness. |

## Environment Tuning

| Environment | Suggested posture |
|---|---|
| Local development | Short thresholds are fine; the goal is fast feedback. |
| CI/integration | Keep thresholds deterministic enough to avoid flaky timing failures. |
| User-facing production-preview | Alert on lag quickly; users feel delay. |
| Batch workloads | Higher lag may be acceptable; focus on completion windows and DLQ rate. |
| Downstream-limited workloads | Lower `MaxWorkers` and alert on throttling before adding workers. |
| High-volume queues | Prefer queue-specific thresholds and cardinality budgets; avoid alerting on raw depth alone. |

## What ChokaQ Does Not Decide For You

ChokaQ can tell you a queue is slow, a worker is stale, or a failure class is
spiking. It cannot know your business impact. You still decide:

- which queues are user-facing;
- which failures are safe to retry;
- which handlers have non-idempotent side effects;
- which downstream systems have quotas;
- how long delayed batch work may wait;
- when purge is acceptable.

Use ChokaQ's signals as the control plane, then encode your application's
business meaning in alerts and runbooks.

