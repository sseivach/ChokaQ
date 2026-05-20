# Retry And DLQ Lifecycle

![Retry and DLQ lifecycle](/diagrams/12-retry-dlq-lifecycle.png)

Retries and DLQ are separate decisions.

A retry says: "This job may succeed later."  
DLQ says: "Automatic execution should stop; a human or a repair workflow must
inspect this job."

ChokaQ keeps that distinction explicit so transient outages do not become
operator noise, and permanent failures do not burn worker capacity forever.

## Normal Retry Path

1. A worker moves the job from `Fetched` to `Processing`.
2. The handler throws an exception.
3. The processor classifies the failure.
4. If the failure is retryable and the retry budget remains, ChokaQ calculates a future `ScheduledAtUtc`.
5. The row remains in `JobsHot`.
6. Workers skip the row until it becomes due.
7. The next execution increments `AttemptCount` when it crosses into `Processing`.

Retry scheduling is stored in SQL. No sleeping thread owns the delay.

## DLQ Path

A job moves to `JobsDLQ` when automatic processing is no longer the right
decision. Common reasons include:

| Reason | Meaning | Operator action |
|---|---|---|
| `MaxRetriesExceeded` | Retry budget was exhausted. | Inspect error, fix dependency or payload, then resurrect if safe. |
| `FatalError` | The failure was classified as non-transient. | Fix code/data before retrying. |
| `Cancelled` | An operator or runtime path cancelled the job. | Confirm cancellation was intentional. |
| `Zombie` | Processing heartbeat expired. | Check side effects before resurrection. |
| `CircuitBreakerOpen` | The runtime refused execution because the circuit was open. | Fix downstream or wait for recovery. |
| `Throttled` | Downstream asked the system to slow down. | Respect retry-after or tune traffic. |
| `RetryLifetimeExpired` | Job age exceeded policy. | Treat as stale work. |

DLQ is not just an error table. It is the operational queue for work that needs
judgment.

## Retry Delay Inputs

Retry delay uses policy from `ChokaQ:Retry`:

| Setting | Purpose |
|---|---|
| `MaxAttempts` | Bounds how many executions can start. |
| `BaseDelay` | Initial delay after a transient failure. |
| `BackoffMultiplier` | Exponential growth factor. |
| `MaxDelay` | Hard cap for retry delay. |
| `JitterMaxDelay` | Random spread to avoid synchronized retry storms. |
| `CircuitBreakerDelay` | Delay when execution is blocked by an open breaker. |
| `MaxJobAge` | Prevents very old jobs from retrying forever. |

## Why DLQ Exists

Without DLQ, a queue engine has two bad options:

- keep retrying bad work forever;
- drop bad work silently.

ChokaQ chooses a third option: preserve the failed job with its payload,
failure reason, error details, attempt count, queue, type key, and timestamps.
The operator can then inspect, edit, resurrect, purge, or leave it for audit.

## Architecture Decision

### Why this pattern?

At-least-once systems need a durable terminal state for work that automatic
execution cannot safely complete. DLQ provides that terminal state without
destroying evidence.

### Trade-offs

DLQ creates an operational responsibility. Someone must monitor it and decide
what to do. The benefit is explicit evidence and operator control instead of
unbounded retry churn around the same poison message.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Infinite retries | Simple to implement. | Retry storms, extra worker usage, hidden poison jobs. |
| Drop failed jobs | Keeps queues clean. | Loses accepted work and destroys audit evidence. |
| External error topic only | Works in broker systems. | Still needs storage, tooling, and operator workflow. |

### When not to use this approach

For disposable telemetry events where loss is acceptable, a durable DLQ may be
too heavy. For business workflows, payments, emails, reports, and integrations,
DLQ is the safer default.

### Interview questions

**Why not retry forever?**  
Because permanent failures become retry storms and consume capacity that healthy
jobs need.

**Why do zombies go to DLQ instead of retry?**  
Because the handler may have already produced side effects. Blind retry can
duplicate emails, charges, or external writes.

**What makes retry safe?**  
Only handler idempotency and downstream idempotency make external side effects
safe. ChokaQ can preserve and reschedule work, but it cannot make arbitrary
side effects exactly-once.
