# Worker Autoscaling Guidance

ChokaQ does not ship an adaptive autoscaler in the preview. Scale workers from
host metrics and ChokaQ signals, with SQL Server mode as the durable backlog.

## Primary Signals

| Signal | Scale interpretation |
|---|---|
| `chokaq.jobs.queue_lag` | Primary saturation signal. Rising lag means jobs wait too long before execution. |
| `chokaq.workers.active` | Shows active execution pressure per queue. |
| `chokaq.jobs.failed` and `chokaq.jobs.dlq` | Scale only after checking whether failures are capacity-related, dependency-related, or payload-related. |
| `chokaq.circuits.events` | Open/rejected circuits mean scaling may increase pressure on an unhealthy dependency. |
| Worker health check | A stale worker heartbeat means the host may be stuck or stopped. |
| SQL latency and locks | Scale SQL or reduce workers before adding more hosts when storage is saturated. |

## Practical Policy

1. Scale out when queue lag is above the service objective and SQL/downstream
   health is normal.
2. Hold or scale in when circuits are open for the dominant job type.
3. Lower queue `MaxWorkers` when one dependency needs isolation from other
   workloads.
4. Prefer separate queues for workloads with different latency, concurrency, or
   timeout requirements.
5. Keep SQL worker prefetch memory bounded; `JobsHot` is the durable backlog,
   not process memory.

## KEDA / HPA Inputs

For Kubernetes, expose metrics through the host application's OpenTelemetry
exporter. Reasonable autoscaling inputs are:

- p95 or max `chokaq.jobs.queue_lag` by queue.
- active workers vs configured capacity.
- DLQ rate by reason.
- circuit open/reject event rate.
- SQL health and command latency from the host/database monitoring stack.

Do not use queue depth alone. Ten thousand tiny due jobs and twenty old blocked
jobs require different responses; lag captures the user-visible delay.
