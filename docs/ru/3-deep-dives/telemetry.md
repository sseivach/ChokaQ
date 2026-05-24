# Telemetry

![Telemetry signal flow](/diagrams/36-telemetry-signal-flow.png)

Telemetry - объединенная runtime signal surface: metrics, logs, health checks, The Deck notifications и durable dashboard aggregates.

ChokaQ разделяет telemetry по назначению. Metrics нужны для trends и alerting. Logs - для event evidence. Health checks - для infrastructure decisions. The Deck - для live operator workflow. SQL state остается source of truth.

## Signal types

| Signal | Purpose |
|---|---|
| OpenTelemetry metrics | Alerting, dashboards, autoscaling, SLOs. |
| Structured logs | Debugging and incident timelines. |
| Health checks | Load balancer and infrastructure health. |
| SignalR notifications | Real-time operator updates in The Deck. |
| `MetricBuckets` | Durable recent throughput/failure aggregates. |
| SQL tables | Authoritative job state. |

## Important events

Runtime логирует worker lifecycle, retry scheduling, DLQ moves, heartbeat failures, circuit breaker events, zombie rescue, state-transition conflicts и prefetched-job release problems.

Эти events должны быть searchable по job ID, queue, job type и worker ID, где они доступны.

## Health vs metrics

Health checks отвечают: "Должна ли infrastructure считать этот host healthy?"

Metrics отвечают: "Что меняется со временем?"

Host может быть технически healthy, пока queue lag растет. Поэтому production operations должны alert'ить по lag и failure-rate metrics, а не только по HTTP health status.

## Архитектурное решение

### Почему этот pattern?

Одной telemetry surface недостаточно. Background job systems нужны и machine-readable signals, и operator-readable state.

### Trade-offs

Больше signals означает больше documentation и alert design. Выигрыш - ниже incident ambiguity: operators видят, проблема в queue lag, SQL connectivity, handler failures, heartbeat failures или circuit protection.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Logs only | Легко emit'ить. | Сложно alert'ить и точно aggregate'ить. |
| Metrics only | Хорошо для SLOs. | Слабее payload/state investigation. |
| Dashboard only | Operator friendly. | Нужны metrics или logs для automated alerting. |

### Дополнительные вопросы

**Что является source of truth?**  
SQL storage authoritative для job state. Metrics и SignalR - derived signals.

**Какой первый saturation signal?**  
Queue lag, потому что он измеряет, как долго eligible work ждет.

**Зачем `MetricBuckets`, если есть OpenTelemetry?**  
The Deck нужны быстрые local rolling windows из durable state без зависимости от external metrics backend.
