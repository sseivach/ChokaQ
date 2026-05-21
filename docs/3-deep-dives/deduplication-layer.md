# Deduplication Layer

![Deduplication layer](/diagrams/58-deduplication-layer.png)

The deduplication layer is a lightweight active-key guard. It is separate from
durable enqueue idempotency and from handler idempotency middleware.

Its role is to suppress short-lived duplicate actions such as double-clicks,
retry storms, or repeated local attempts before they hit a heavier boundary.

## Where It Lives

| Runtime type | Role |
|---|---|
| `IDeduplicator` | Internal contract for time-limited key acquisition. |
| `InMemoryDeduplicator` | Process-local implementation using `ConcurrentDictionary` and timer cleanup. |

## Behavior

`TryAcquireAsync(key, ttl)` returns:

- `true` when the key is new or expired;
- `false` when the key is already active.

Expired keys are removed by a background cleanup timer.

## Correctness Boundary

The in-memory deduplicator is process-local. It does not provide durable or
cross-host exactly-once behavior. Do not use it as a substitute for SQL
idempotency keys or handler idempotency.

## Architecture Decision

### Why this pattern?

Some duplicate suppression is useful before reaching storage or expensive
runtime paths. A small in-memory guard is enough for local burst suppression
without adding infrastructure.

### Trade-offs

Process-local deduplication does not work across multiple hosts and disappears
on restart. That is acceptable only for soft suppression, not correctness.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Durable SQL dedupe for everything | Stronger. | More database writes for soft duplicate suppression. |
| Distributed cache lock | Cross-host. | Extra infrastructure and failure modes. |
| No dedupe layer | Simpler. | More repeated local work under bursts. |

### Additional Questions

**Is this exactly-once protection?**  
No. It is a soft process-local duplicate guard.

**What should protect business side effects?**  
Handler idempotency and downstream/provider idempotency.

**Why have this layer at all?**  
To cheaply reduce repeated local actions before they become heavier storage or
execution work.

