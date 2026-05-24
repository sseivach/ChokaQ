# Worker Autoscaling Guidance

ChokaQ не поставляет adaptive autoscaler в preview. Scale workers по host metrics и ChokaQ signals, используя SQL Server mode как durable backlog.

## Primary signals

| Signal | Scale interpretation |
|---|---|
| `chokaq.jobs.queue_lag` | Primary saturation signal. Rising lag means jobs wait too long before execution. |
| `chokaq.workers.active` | Shows active execution pressure per queue. |
| `chokaq.jobs.failed` and `chokaq.jobs.dlq` | Scale only after checking whether failures capacity-related, dependency-related or payload-related. |
| `chokaq.circuits.events` | Open/rejected circuits mean scaling may increase pressure on unhealthy dependency. |
| Worker health check | Stale worker heartbeat means host may be stuck or stopped. |
| SQL latency and locks | Scale SQL or reduce workers before adding more hosts when storage saturated. |

## Practical policy

1. Scale out, когда queue lag выше service objective, а SQL/downstream health normal.
2. Hold или scale in, когда circuits open для dominant job type.
3. Lower queue `MaxWorkers`, когда dependency нужна isolation from other workloads.
4. Prefer separate queues для workloads с разными latency, concurrency или timeout requirements.
5. Keep SQL worker prefetch memory bounded; `JobsHot` - durable backlog, not process memory.

## KEDA / HPA inputs

Для Kubernetes expose metrics через host application's OpenTelemetry exporter. Reasonable autoscaling inputs:

- p95 или max `chokaq.jobs.queue_lag` by queue.
- active workers vs configured capacity.
- DLQ rate by reason.
- circuit open/reject event rate.
- SQL health и command latency from host/database monitoring stack.

Не используйте только queue depth. Ten thousand tiny due jobs и twenty old blocked jobs требуют разных responses; lag captures user-visible delay.
