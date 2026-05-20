# Prefetching

![Prefetch worker pipeline](/diagrams/25-prefetch-worker-pipeline.png)

Prefetching decouples SQL polling from handler execution. The SQL worker fetches
small batches from `JobsHot` into a local buffer, then processors consume that
buffer under a dynamic concurrency limiter.

This improves throughput without turning SQL Server into a tight per-job
round-trip bottleneck.

## Where It Lives

SQL prefetching lives in `SqlJobWorker`.

The worker has two loops:

| Loop | Responsibility |
|---|---|
| Fetcher loop | Claims eligible rows from SQL and writes them into the local prefetch buffer. |
| Processor loop | Reads prefetched rows, waits for a concurrency permit, validates the execution lease, then dispatches user code. |

## Happy Path

1. Fetcher checks queue configuration.
2. Fetcher claims due pending jobs through `FetchNextBatch`.
3. Claimed rows enter the bounded channel.
4. Processor reads a row from the channel.
5. Processor waits for concurrency capacity.
6. Processor calls `MarkAsProcessing`.
7. Handler executes.
8. Job moves to Archive, retry schedule, or DLQ.

## Stale Prefetch Protection

Prefetched work is not enough to execute user code. Before dispatching the
handler, ChokaQ performs `MarkAsProcessing`. That SQL update validates that the
row still belongs to the worker and is still in the expected fetched state.

If the row was released, paused, reclaimed, or moved, `MarkAsProcessing` affects
zero rows and user code does not run.

## Failure Path

If the host shuts down or a queue pauses while rows are prefetched, the worker
releases rows back to pending where possible. If release times out, abandoned
fetch recovery can reclaim them later because fetched rows have not executed
user code.

## Architecture Decision

### Why this pattern?

Without prefetching, every handler slot would be coupled to SQL polling latency.
With prefetching, SQL can claim work in small batches while execution proceeds
in parallel.

### Trade-offs

Prefetching creates a temporary local ownership window. ChokaQ compensates with
a bounded buffer, release-on-shutdown behavior, abandoned-fetch recovery, and
the final `MarkAsProcessing` lease gate.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Fetch one job per handler slot | Simpler. | More SQL round trips and weaker throughput under latency. |
| Fetch huge batches | Fewer SQL calls. | More stale ownership and memory risk. |
| Broker push model | Great for some workloads. | Different infrastructure and less direct SQL operational state. |

### Interview questions

**What is the main risk of prefetching?**  
The worker can hold jobs locally that it has not started yet. ChokaQ bounds that
risk with a small channel and validates the lease before execution.

**Why not count fetch as an attempt?**  
Because user code has not run yet. Attempts increment when the row moves to
`Processing`.

**What happens if a prefetched job is never processed?**  
Fetched-job recovery resets stale fetched rows back to pending.

