# Metrics

![Metrics instruments and tags](/diagrams/37-metrics-instruments-tags.png)

ChokaQ exposes metrics through the .NET `System.Diagnostics.Metrics` API. The
meter name is `ChokaQ`.

Metrics are a public production contract. Names, units, and tag cardinality
need to be stable enough for dashboards and alerts.

## Instruments

| Metric | Type | Meaning |
|---|---|---|
| `chokaq.jobs.enqueued` | Counter | Jobs accepted into the queue. |
| `chokaq.jobs.completed` | Counter | Jobs completed successfully. |
| `chokaq.jobs.failed` | Counter | Handler execution failures. |
| `chokaq.jobs.processing_duration` | Histogram, ms | Handler processing duration. |
| `chokaq.jobs.queue_lag` | Histogram, ms | Time eligible jobs waited before processing. |
| `chokaq.jobs.dlq` | Counter | Jobs moved to DLQ. |
| `chokaq.jobs.retried` | Counter | Jobs scheduled for retry. |
| `chokaq.workers.active` | UpDownCounter | Current active processing workers. |
| `chokaq.jobs.heartbeat_failures` | Counter | Failed heartbeat writes. |
| `chokaq.jobs.state_transition_conflicts` | Counter | Worker-owned transitions that affected no rows. |
| `chokaq.idempotency.claims` | Counter | Idempotency claim outcomes. |
| `chokaq.circuits.events` | Counter | Circuit breaker state events. |

## Cardinality Control

Metrics tags are capped by `ChokaQ:Metrics` options:

- max queue tag values;
- max job type tag values;
- max error tag values;
- max failure reason tag values;
- max tag value length;
- unknown value label;
- overflow value label.

When a process sees too many distinct values, new values collapse into the
overflow label instead of creating unlimited time series.

## Alerting Guidance

| Signal | Use |
|---|---|
| Queue lag p95/p99 | Primary saturation alert. |
| DLQ rate | Terminal failure alert. |
| Retry rate | Downstream instability signal. |
| Heartbeat failures | Storage or worker health issue. |
| State transition conflicts | Ownership/race/recovery signal. |
| Circuit events | Systemic downstream failure protection. |

## Architecture Decision

### Why this pattern?

OpenTelemetry-compatible metrics let ChokaQ integrate with existing monitoring
without taking a dependency on a specific vendor.

### Trade-offs

Tag cardinality must be controlled. Queue names, job types, and error values
can become unbounded if applications feed dynamic strings into them.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Vendor-specific SDK | Rich features. | Locks users into one observability stack. |
| Logs-derived metrics | Flexible. | Slower and more expensive for alerting. |
| No built-in metrics | Small runtime surface. | Weak production story. |

### Interview questions

**Why is queue lag more important than queue depth?**  
Depth lacks time context. Lag tells you how long eligible work is actually
waiting.

**Why cap tag values?**  
To prevent accidental high-cardinality time series from damaging the monitoring
backend.

**What metric indicates duplicate-protection behavior?**  
`chokaq.idempotency.claims`, split by outcome.

