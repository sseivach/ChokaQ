# Metrics

![Metrics instruments and tags](/diagrams/37-metrics-instruments-tags.png)

ChokaQ публикует metrics через .NET API `System.Diagnostics.Metrics`. Meter name - `ChokaQ`.

Metrics - public production contract. Names, units и tag cardinality должны быть достаточно стабильны для dashboards и alerts.

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

## Cardinality control

Metrics tags ограничиваются options `ChokaQ:Metrics`:

- max queue tag values;
- max job type tag values;
- max error tag values;
- max failure reason tag values;
- max tag value length;
- unknown value label;
- overflow value label.

Когда process видит слишком много distinct values, новые values сворачиваются в overflow label вместо создания unlimited time series.

## Alerting guidance

| Signal | Use |
|---|---|
| Queue lag p95/p99 | Primary saturation alert. |
| DLQ rate | Terminal failure alert. |
| Retry rate | Downstream instability signal. |
| Heartbeat failures | Storage or worker health issue. |
| State transition conflicts | Ownership/race/recovery signal. |
| Circuit events | Systemic downstream failure protection. |

## Архитектурное решение

### Почему этот pattern?

OpenTelemetry-compatible metrics позволяют ChokaQ интегрироваться с существующим monitoring без зависимости от конкретного vendor.

### Trade-offs

Tag cardinality должен контролироваться. Queue names, job types и error values могут стать unbounded, если applications передают в них dynamic strings.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Vendor-specific SDK | Rich features. | Привязывает users к одному observability stack. |
| Logs-derived metrics | Flexible. | Медленнее и дороже для alerting. |
| No built-in metrics | Маленький runtime surface. | Слабый production story. |

### Дополнительные вопросы

**Почему queue lag важнее queue depth?**  
Depth не содержит time context. Lag показывает, сколько eligible work реально ждет.

**Зачем ограничивать tag values?**  
Чтобы accidental high-cardinality time series не повредили monitoring backend.

**Какая metric показывает duplicate-protection behavior?**  
`chokaq.idempotency.claims`, разбитая по outcome.
