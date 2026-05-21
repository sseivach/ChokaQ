# Bounded Prefetch

![Bounded prefetch capacity](/diagrams/26-bounded-prefetch-capacity.png)

Bounded prefetch is the pressure boundary between durable SQL backlog and local
process memory. ChokaQ intentionally keeps the SQL worker prefetch buffer small
and bounded.

## Why Bounded Matters

An unbounded local buffer can turn a database-backed queue into a memory-backed
queue by accident. If a worker claims thousands of jobs and then slows down,
other workers cannot see those rows and the owning process may run out of
memory.

Bounded prefetch keeps SQL as the backlog.

## Capacity Behavior

The SQL worker uses a bounded `Channel<JobHotEntity>`.

When the channel has room:

- the fetcher claims due jobs from SQL;
- claimed rows are written to the buffer;
- processors consume rows as permits become available.

When the channel is full:

- the fetcher waits;
- no more SQL rows are claimed;
- backlog remains visible in `JobsHot`.

## Operational Meaning

Bounded prefetch gives operators a more honest view:

- if workers are slow, queue lag grows in SQL;
- if SQL is empty but workers are busy, the buffer is draining;
- if fetched rows are stale, recovery can reset them.

## Architecture Decision

### Why this pattern?

The durable backlog should stay in durable storage. Local process memory is only
a smoothing buffer, not the source of truth.

### Trade-offs

A small buffer may reduce peak throughput when SQL latency is high. A large
buffer increases stale ownership and shutdown recovery work. The right value is
the smallest buffer that keeps processors fed under normal latency.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Unbounded channel | Maximum short-term absorption. | Memory risk and hidden backlog. |
| No buffer | Simpler correctness. | More SQL latency on the execution path. |
| Per-queue buffers | More isolation. | More complexity and tuning surface. |

### Additional Questions

**What does bounded prefetch protect?**  
Process memory, SQL visibility, and fairness across workers.

**Can bounded prefetch reduce throughput?**  
Yes, if too small for the workload and SQL latency. That is a tuning trade-off,
not a correctness issue.

**Where should backlog live?**  
In `JobsHot`, because SQL is durable and observable.

