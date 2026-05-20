# Health Checks

![Health checks](/diagrams/52-health-checks.png)

ChokaQ health checks answer whether a host and its durable queue boundary are
usable from an infrastructure point of view.

They do not replace metrics. Health checks are point-in-time status; metrics
show trends.

## Health Surfaces

| Check | Purpose |
|---|---|
| SQL connectivity | Can the host open SQL and see required ChokaQ tables? |
| Worker heartbeat | Did the local worker loop enter and keep cycling? |
| Queue saturation | Is eligible work waiting longer than the configured budget? |

## SQL Connectivity

The SQL health check verifies:

- connection open succeeds;
- configured schema exists;
- required tables exist: `JobsHot`, `JobsArchive`, `JobsDLQ`, `StatsSummary`,
  `MetricBuckets`, and `Queues`;
- command timeout policy is applied.

Connectivity alone is not enough. A database can be reachable but missing the
runtime schema.

## Worker Heartbeat

The worker health check is process-local. It reports:

- active worker count;
- total worker capacity;
- running state;
- last worker heartbeat;
- heartbeat age.

This catches a host whose SQL connection is fine but whose background service is
not actually running.

## Queue Saturation

Queue saturation uses lag, not just depth. It looks at eligible pending work and
reports degraded/unhealthy when the worst queue exceeds configured thresholds.

Depth asks: "How many jobs exist?"  
Lag asks: "How long has actionable work waited?"

## Architecture Decision

### Why this pattern?

Background job health has multiple dimensions. A single "SQL reachable" check
cannot prove that workers are alive or that the system is keeping up.

### Trade-offs

Queue lag health can mark a host degraded even when the process is technically
alive. That is intentional: a queue processor that cannot keep up is an
operational problem.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Liveness-only endpoint | Simple. | Misses broken workers and saturated queues. |
| SQL-only endpoint | Validates storage. | Misses dead background loops. |
| Metrics-only alerting | Great for trends. | Not ideal for readiness probes. |

### Interview questions

**Why use queue lag instead of depth?**  
Depth lacks job duration context. Lag measures user-visible waiting time for
eligible work.

**Can health be green while DLQ grows?**  
Yes. DLQ growth is usually an alert/metric problem, not necessarily a readiness
failure.

**Should health endpoints be public?**  
No. They should be infrastructure-scoped or protected.

