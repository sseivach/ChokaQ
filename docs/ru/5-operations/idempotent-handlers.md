# Idempotent Handler Checklist

![Idempotency key flow](/diagrams/34-idempotency-key-flow.png)

ChokaQ предоставляет at-least-once execution. Handler может run more than once, если worker crash'нется после side effect в user code, но до stored finalization. Делайте каждый side-effecting handler idempotent.

## Required decisions

| Question | Recommended answer |
|---|---|
| What is the business idempotency key? | Используйте order ID, invoice ID, webhook event ID, payment attempt ID или другой stable domain key. |
| Where is the key enforced? | Prefer system that owns side effect: database unique constraint, provider idempotency key или outbox. |
| How long can duplicates arrive? | Set idempotency TTL from business reality, not from retry settings alone. |
| What happens after partial side effects? | Store enough state to retry safely or send the job to manual review. |

## Patterns

- Database unique constraint plus upsert.
- Payment/email/webhook provider idempotency key.
- Outbox table для local transaction plus later publish.
- Read-before-write, когда повтор handler converges to same state.
- Manual review для operations, которые нельзя безопасно repeat.

## ChokaQ middleware boundary

Optional idempotency middleware пишет `InProgress` claim before handler execution, skips concurrent duplicates и пишет completion marker after success. Оно уменьшает duplicate handler execution для одного active key, но не делает external systems exactly-once.

Relevant metrics:

| Metric | Outcome examples |
|---|---|
| `chokaq.idempotency.claims` | `claimed`, `completed`, `completed_duplicate`, `in_progress_duplicate`, `released`, `complete_conflict`, `empty_key` |

Investigate `complete_conflict`: это значит, что handler execution finished, но completion marker не удалось сохранить для этого key/job pair.

## Архитектурное решение

### Почему этот pattern?

At-least-once execution честен, но переносит duplicate side-effect safety на business boundary. Idempotent handlers - application-level answer.

### Trade-offs

Idempotency добавляет data modeling и retention decisions. Для payments, email, webhooks и external writes эта цена необходима.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Trust queue exactly-once | Просто. | False assumption for real crashes. |
| Disable retries | Avoids retry duplicates. | Loses resilience и все равно не решает crash windows. |
| Provider idempotency only | Strong where available. | Не every dependency supports it. |

### Дополнительные вопросы

**Почему handler idempotency required?**  
Потому что job может execute more than once after crash/recovery.

**Самое сложное duplicate window?**  
External side effect succeeds, then process crashes before ChokaQ archives the job.

**Что использовать payment jobs?**  
Provider-supported idempotency key, tied to business payment attempt.
