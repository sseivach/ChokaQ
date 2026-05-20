# Scaling Model

![Scaling model](/diagrams/65-scaling-model.png)

ChokaQ scales by adding workers, tuning concurrency, controlling per-queue
capacity, and keeping SQL queries/indexes healthy.

## Scaling Dimensions

| Dimension | What it changes |
|---|---|
| More app instances | More fetchers and processors. |
| Higher worker capacity | More concurrent handler execution. |
| Per-queue max workers | Fairness and downstream protection. |
| Retry/backoff tuning | Pressure during failure. |
| SQL sizing/indexes | Storage coordination throughput. |

## Primary Saturation Signal

Queue lag is the main signal. Depth can be misleading because ten slow jobs can
be worse than ten thousand tiny jobs. Lag measures how long eligible work waits.

## Bottlenecks

| Bottleneck | Symptom | First response |
|---|---|---|
| Handler/downstream slow | Active workers high, lag rising. | Increase downstream capacity or reduce per-queue workers. |
| SQL latency | Fetch/retry/heartbeat delays, health degraded. | Check indexes, waits, command timeouts. |
| Poison jobs | DLQ grows by type/reason. | Fix handler/payload; avoid bulk retry storm. |
| Under-provisioned workers | Pending lag rises while SQL is healthy. | Add instances or increase capacity. |
| Bad retry policy | Retry rate spikes and lag grows. | Increase backoff/jitter, use circuit breaker. |

## Horizontal Scaling

Multiple workers can fetch concurrently because SQL row locks and ownership
predicates coordinate claims. More workers help until SQL or downstream systems
become the bottleneck.

## Interview Questions

**What happens at 10x load?**  
First watch queue lag, SQL waits, active workers, and downstream latency. Add
workers only if SQL/downstream are not already saturated.

**What happens at 100x load?**  
You may need provider specialization, partitioning, stricter queue isolation, or
a broker/log architecture depending on workload shape.

**How would you autoscale?**  
Use queue lag and processing duration, not just CPU or queue depth.

