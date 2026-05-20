# Failure Taxonomy

![Failure taxonomy](/diagrams/59-failure-taxonomy.png)

Failure taxonomy turns raw exceptions into operational meaning. ChokaQ stores a
`FailureReason` when a job lands in DLQ so operators can triage by category
instead of reading every stack trace first.

## Why Taxonomy Matters

Two failed jobs can require very different actions:

- a transient HTTP timeout may be safe to retry;
- a malformed payload should be fixed before retry;
- a zombie job may have already sent an email or charged a card;
- an admin-cancelled job may be intentionally stopped.

The taxonomy preserves that distinction in data.

## Common Reasons

| Reason | Meaning | Typical response |
|---|---|---|
| `MaxRetriesExceeded` | Retry budget exhausted. | Fix cause, then sample resurrection. |
| `FatalError` | Classified as non-transient. | Fix code or payload first. |
| `Timeout` | Handler exceeded timeout. | Tune handler or timeout; inspect side effects. |
| `Cancelled` | Operator/runtime cancellation. | Confirm intent. |
| `Zombie` | Heartbeat expired during processing. | Inspect side effects before retry. |
| `CircuitBreakerOpen` | Execution blocked by open circuit. | Fix downstream before forcing recovery. |
| `Throttled` | Downstream overload/rate limit. | Respect retry-after and reduce pressure. |
| `Transient` | Retryable family eventually exhausted. | Check downstream instability. |
| `HeartbeatFailure` | Heartbeat write failure crossed policy. | Check SQL/storage pressure. |
| `RetryLifetimeExpired` | Job became too old to retry. | Treat as stale work. |

## Smart Worker Relationship

Smart Worker classification decides whether a failure should retry or fast-fail
to DLQ. Failure taxonomy is the persisted operator-facing result.

## Architecture Decision

### Why this pattern?

Operators need routing metadata. Raw exception text is useful for debugging, but
not enough for safe bulk actions, dashboards, or incident triage.

### Trade-offs

Classification can be wrong if application exceptions are too generic. Apps
should throw meaningful fatal/throttled/transient signals where possible.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Store stack traces only | Simple. | Poor filtering and unsafe bulk decisions. |
| Use exception type only | Automatic. | Loses operational categories like zombie/cancelled. |
| App-owned categories only | Flexible. | No consistent runtime semantics. |

### Interview questions

**Why persist failure reason separately from error details?**  
Because operators need stable categories for filtering, dashboards, and actions.

**Which failure should not be blindly retried?**  
Zombie, fatal payload/code errors, cancelled jobs, and stale jobs beyond
lifetime policy.

**How does this help The Deck?**  
The Deck can group, filter, badge, and bulk-preview based on reason instead of
free-form exception text.

