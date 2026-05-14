# Idempotent Handler Checklist

ChokaQ provides at-least-once execution. A handler can run more than once if a
worker crashes after user code produces a side effect but before finalization is
stored. Make every side-effecting handler idempotent.

## Required Decisions

| Question | Recommended answer |
|---|---|
| What is the business idempotency key? | Use order ID, invoice ID, webhook event ID, payment attempt ID, or another stable domain key. |
| Where is the key enforced? | Prefer the system that owns the side effect: database unique constraint, provider idempotency key, or outbox. |
| How long can duplicates arrive? | Set idempotency TTL from business reality, not from retry settings alone. |
| What happens after partial side effects? | Store enough state to retry safely or send the job to manual review. |

## Patterns

- Database unique constraint plus upsert.
- Payment/email/webhook provider idempotency key.
- Outbox table for local transaction plus later publish.
- Read-before-write when repeating the handler converges to the same state.
- Manual review for operations that cannot be made safe to repeat.

## ChokaQ Middleware Boundary

The optional idempotency middleware writes an `InProgress` claim before handler
execution, skips concurrent duplicates, and writes a completion marker after
success. It reduces duplicate handler execution for one active key, but it does
not make external systems exactly-once.

Relevant metrics:

| Metric | Outcome examples |
|---|---|
| `chokaq.idempotency.claims` | `claimed`, `completed`, `completed_duplicate`, `in_progress_duplicate`, `released`, `complete_conflict`, `empty_key` |

Investigate `complete_conflict`: it means handler execution finished, but the
completion marker could not be stored for that key/job pair.
