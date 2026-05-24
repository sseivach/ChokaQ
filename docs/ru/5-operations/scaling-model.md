# Scaling Model

![Scaling model](/diagrams/65-scaling-model.png)

ChokaQ scales by adding workers, tuning concurrency, controlling per-queue capacity и keeping SQL queries/indexes healthy.

## Scaling dimensions

| Dimension | What it changes |
|---|---|
| More app instances | More fetchers and processors. |
| Higher worker capacity | More concurrent handler execution. |
| Per-queue max workers | Fairness and downstream protection. |
| Retry/backoff tuning | Pressure during failure. |
| SQL sizing/indexes | Storage coordination throughput. |

## Primary saturation signal

Queue lag - главный signal. Depth может вводить в заблуждение: ten slow jobs могут быть хуже, чем ten thousand tiny jobs. Lag измеряет, как долго eligible work waits.

## Bottlenecks

| Bottleneck | Symptom | First response |
|---|---|---|
| Handler/downstream slow | Active workers high, lag rising. | Increase downstream capacity or reduce per-queue workers. |
| SQL latency | Fetch/retry/heartbeat delays, health degraded. | Check indexes, waits, command timeouts. |
| Poison jobs | DLQ grows by type/reason. | Fix handler/payload; avoid bulk retry storm. |
| Under-provisioned workers | Pending lag rises while SQL healthy. | Add instances or increase capacity. |
| Bad retry policy | Retry rate spikes and lag grows. | Increase backoff/jitter, use circuit breaker. |

## Horizontal scaling

Multiple workers могут fetch concurrently, потому что SQL row locks и ownership predicates coordinate claims. More workers помогают, пока SQL или downstream systems не станут bottleneck.

## Дополнительные вопросы

**Что будет при 10x load?**  
Сначала смотрите queue lag, SQL waits, active workers и downstream latency. Добавляйте workers только если SQL/downstream еще не saturated.

**Что будет при 100x load?**  
Может понадобиться provider specialization, partitioning, stricter queue isolation или broker/log architecture, в зависимости от workload shape.

**Как autoscale?**  
Используйте queue lag и processing duration, а не только CPU или queue depth.
