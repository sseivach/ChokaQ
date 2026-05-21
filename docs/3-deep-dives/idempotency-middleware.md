# Idempotency Middleware

![Idempotency middleware claims](/diagrams/35-idempotency-middleware-claims.png)

Idempotency middleware protects handler side effects. It is different from
enqueue idempotency.

Enqueue idempotency prevents duplicate active rows. Handler idempotency prevents
duplicate business effects when a job is executed more than once.

## Where It Lives

The middleware implementation is `ChokaQ.Core.Idempotency.IdempotencyMiddleware`.
It runs inside the execution middleware pipeline around the handler.

Jobs opt in by implementing `IIdempotentJob`.

## Claim Flow

1. Middleware checks whether the job implements `IIdempotentJob`.
2. It reads `IdempotencyKey`.
3. It calls `IIdempotencyClaimStore.TryBeginAsync`.
4. If the key is already completed, the handler is skipped.
5. If the key is already in progress, the handler is skipped.
6. If the key is claimed, the handler runs.
7. On handler failure, the in-progress claim is released.
8. On handler success, a completed marker is stored.

## Outcomes

| Outcome | Meaning |
|---|---|
| `claimed` | This execution owns the key. |
| `completed_duplicate` | Work was already completed; skip handler. |
| `in_progress_duplicate` | Another execution owns the key; skip handler. |
| `released` | Handler failed and claim was released. |
| `completed` | Completion marker was stored. |
| `complete_conflict` | Completion failed; this is a correctness signal. |

## TTL Policy

`InProgressTtl` bounds how long an abandoned claim blocks work. Result TTL can
be set per job or through global defaults and min/max policy.

TTL is a business decision. Payment keys may need longer retention than low-risk
notifications.

## Architecture Decision

### Why this pattern?

At-least-once delivery means handlers may run more than once. Middleware gives
applications a central place to guard side effects without embedding duplicate
claim logic in every handler.

### Trade-offs

Idempotency middleware depends on a correct claim store. If the claim store is
not durable enough for the side effect being protected, duplicates can still
escape.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Enqueue idempotency only | Simple. | Does not protect retry/crash duplicate execution. |
| Handler-only manual dedupe | Flexible. | Repeated boilerplate and inconsistent behavior. |
| Downstream provider idempotency only | Strong for supported APIs. | Not every side effect has provider keys. |

### Additional Questions

**Why is enqueue idempotency not enough?**  
Because a single accepted job can execute twice after a crash or recovery.

**What if the handler succeeds but completion marker fails?**  
The system may run the handler again. The downstream side effect still needs its
own idempotency key where duplicates are unacceptable.

**Why release the claim on handler failure?**  
Because retry should be allowed to attempt the work again after transient
failure.

