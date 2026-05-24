# SLOs And Alerts

Эта страница объясняет operational signals, которые ChokaQ exposes, что они значат и как превращать их в useful alerts. Она написана для двух аудиторий:

- developers, которым нужно понимать, что делают background jobs;
- operators, которым нужно решить: scale, investigate, retry или stop.

ChokaQ - background job engine. Его health - не только "process up?". Process может быть alive, пока jobs ждут слишком долго, workers stuck, SQL slow или downstream provider rejects calls. Хороший monitoring начинается с user-visible delay и failure rate.

## Terms

| Term | Meaning |
|---|---|
| Eligible job | Job, whose scheduled time has arrived and can be fetched by workers. |
| Queue lag | Как долго eligible pending work ждала до execution. |
| DLQ | Dead Letter Queue: failed, cancelled, zombie или poison work, требующая operator attention. |
| Worker heartbeat | Process-local signal, что background worker loop alive. |
| Job heartbeat | Per-job signal, что `Processing` job still alive. |
| Failure reason | Structured class вроде `Fatal`, `Throttled`, `Timeout`, `Zombie` или `Cancelled`. |
| Circuit | Per-job-type breaker, который может block repeated failing execution attempts. |

## Default objectives

Это starting points, а не universal law. User-facing notification queue и overnight report queue не должны иметь одинаковый objective.

| Objective | Default target | Why it matters |
|---|---:|---|
| Queue processing latency | 99% eligible jobs start within 5 seconds | Ближайший signal к "users are waiting too long." |
| DLQ rate | Less than 0.1% completed attempts over 5 minutes | Rising DLQ rate значит, что work уходит из normal path. |
| Worker liveness | At least one healthy worker loop for active queues | Jobs не drain'ятся, если workers stopped или stuck. |
| SQL storage health | SQL health check remains healthy | SQL mode зависит от storage для admission, fetch, finalization и dashboard reads. |
| Queue saturation health | Queue lag stays below unhealthy threshold | Saturated queues могут быть alive, но слишком slow для service expectations. |

Для low-priority batch workloads 5-second lag target может быть слишком строгим. Для customer-facing security, payment или notification jobs - слишком мягким. Считайте эти defaults preview baseline и tune per queue.

## Primary signals

ChokaQ exposes три слоя signals:

1. Health checks для yes/no platform decisions.
2. Metrics для trends и alerting.
3. The Deck для human triage и recovery.

### Health checks

SQL mode registers:

| Check | Meaning |
|---|---|
| `chokaq_sql` | SQL Server reachable, ChokaQ storage может query expected objects. |
| `chokaq_worker` | Hosted worker loop running и имеет recent heartbeat. |
| `chokaq_queue_saturation` | Pending queue lag ниже configured degraded/unhealthy thresholds. |

Используйте health checks для readiness и basic monitoring. Не полагайтесь только на health checks для incident diagnosis; они намеренно summarize detail.

### Metrics

Meter `ChokaQ` exposes:

| Instrument | Use it for |
|---|---|
| `chokaq.jobs.enqueued` | Producer activity и queue arrival rate. |
| `chokaq.jobs.completed` | Successful throughput. |
| `chokaq.jobs.failed` | Handler failures before retry or DLQ outcome. |
| `chokaq.jobs.processing_duration` | Slow handlers и timeout risk. |
| `chokaq.jobs.queue_lag` | Primary saturation signal. |
| `chokaq.jobs.dlq` | Failed work by queue, job type и reason. |
| `chokaq.jobs.retried` | Retry pressure и dependency instability. |
| `chokaq.workers.active` | Active processing pressure. |
| `chokaq.jobs.heartbeat_failures` | SQL/network/host pressure during running jobs. |
| `chokaq.jobs.state_transition_conflicts` | Stale ownership attempts и lease races. |
| `chokaq.idempotency.claims` | Accepted, duplicate, completed или rejected idempotency claims. |
| `chokaq.circuits.events` | Circuit open, half-open, close и rejection activity. |

Metric labels cardinality-capped через `ChokaQ:Metrics`. Это защищает monitoring system от unbounded queue names, job type names, error strings или failure reasons. Overflow values grouped into stable bucket вроде `other`.

### The Deck

The Deck - operator console. Используйте его, чтобы ответить:

- Какие queues lagging?
- Какие jobs active?
- Какие failure reasons dominate DLQ?
- Какие error families repeat?
- Circuits open?
- Queue paused или capped?
- Можно ли failed payload repair и requeue?
- Что произошло после bulk action?

The Deck SignalR-assisted и периодически reconciles from storage. Он предназначен для operational visibility, а не как единственный source of truth. SQL остается durable state boundary.

## Alert matrix

Начните с этих alerts и tune после наблюдения реального workload behavior.

| Alert | Suggested trigger | Likely meaning | First action |
|---|---|---|---|
| Queue lag high | Max queue lag > 10s for 5m | Workers не успевают, queue paused/capped, SQL slow или handlers slow. | Откройте The Deck, найдите queue, проверьте worker count и SQL health. |
| Queue lag high with low failures | Lag rising, DLQ rate normal | Capacity shortage или slow normal work. | Add workers, raise queue `MaxWorkers`, split queues или reduce producer rate. |
| Queue lag high with SQL unhealthy | Lag rising, `chokaq_sql` unhealthy | Storage bottleneck. | Inspect SQL connectivity, waits, locks, CPU, disk и command timeouts before adding workers. |
| DLQ rate high | DLQ rate > 1% for 5m | Work leaves normal path. | Inspect top error families и failure reasons перед requeue. |
| Fatal DLQ spike | `chokaq.jobs.dlq{reason="Fatal"}` jumps | Code defect, incompatible payload, bad data или unsupported contract. | Stop blind retries; fix handler или payload; requeue targeted subset only after repair. |
| Throttled DLQ spike | `reason="Throttled"` jumps | Downstream rate limit или quota exhaustion. | Reduce concurrency, lower queue `MaxWorkers`, wait for quota recovery, then requeue carefully. |
| Timeout DLQ spike | `reason="Timeout"` jumps | Handler exceeds execution timeout или dependency latency changed. | Check handler duration и dependency latency; raise timeout only if side effects idempotent and long runtime expected. |
| Worker unhealthy | Worker heartbeat stale | Process stopped, host overloaded или worker loop blocked. | Check host logs, restart if needed, confirm abandoned `Fetched` jobs recover. |
| Heartbeat failures | `chokaq.jobs.heartbeat_failures` increasing | SQL writes, network, locks или host resources under pressure. | Follow heartbeat pressure runbook; avoid immediate cancellation unless configured intentionally. |
| State transition conflicts | Conflicts spike | Stale worker tried to finalize work it no longer owns. | Check slow handlers, zombie recovery, clock/timeout settings и worker restarts. |
| Circuit open events | Circuit open/reject events rising | One job type repeatedly failing. | Inspect error family и downstream health перед adding capacity to that job type. |

## Safe alert response

Когда alert fires, не меняйте все сразу. Используйте порядок:

1. Определите, проблема в lag, failure, worker liveness или SQL health.
2. Определите affected queue и job type.
3. Проверьте, является ли failure reason retry-safe.
4. Примените минимальный полезный control: scale workers, pause queue, lower queue `MaxWorkers`, repair payloads или requeue targeted subset.
5. После изменения смотрите queue lag, DLQ rate, retries и SQL health.

Самая частая unsafe response - blind requeue. Blind requeue может multiply side effects, hammer downstream dependency или hide real payload defect.

## Recommended dashboards

Первый полезный dashboard:

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

## Environment tuning

| Environment | Suggested posture |
|---|---|
| Local development | Short thresholds are fine; goal is fast feedback. |
| CI/integration | Keep thresholds deterministic enough to avoid flaky timing failures. |
| User-facing production-preview | Alert on lag quickly; users feel delay. |
| Batch workloads | Higher lag may be acceptable; focus on completion windows and DLQ rate. |
| Downstream-limited workloads | Lower `MaxWorkers` and alert on throttling before adding workers. |
| High-volume queues | Prefer queue-specific thresholds and cardinality budgets; avoid alerting on raw depth alone. |

## What ChokaQ does not decide for you

ChokaQ может сказать, что queue slow, worker stale или failure class spiking. Он не знает business impact. Вы все равно решаете:

- какие queues user-facing;
- какие failures safe to retry;
- какие handlers имеют non-idempotent side effects;
- какие downstream systems имеют quotas;
- как долго delayed batch work может wait;
- когда purge acceptable.

Используйте ChokaQ signals как control plane, а business meaning приложения фиксируйте в alerts и runbooks.
