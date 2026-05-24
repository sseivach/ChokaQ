# Idempotency Middleware

![Idempotency middleware claims](/diagrams/35-idempotency-middleware-claims.png)

Idempotency middleware защищает side effects handler'а. Это отличается от enqueue idempotency.

Enqueue idempotency предотвращает duplicate active rows. Handler idempotency предотвращает duplicate business effects, когда job выполняется больше одного раза.

## Где это находится

Реализация middleware - `ChokaQ.Core.Idempotency.IdempotencyMiddleware`. Она выполняется внутри execution middleware pipeline вокруг handler'а.

Jobs opt in через реализацию `IIdempotentJob`.

## Claim flow

1. Middleware проверяет, реализует ли job `IIdempotentJob`.
2. Читает `IdempotencyKey`.
3. Вызывает `IIdempotencyClaimStore.TryBeginAsync`.
4. Если key уже completed, handler пропускается.
5. Если key уже in progress, handler пропускается.
6. Если key claimed, handler выполняется.
7. При handler failure in-progress claim release'ится.
8. При handler success сохраняется completed marker.

## Outcomes

| Outcome | Meaning |
|---|---|
| `claimed` | Это execution владеет key. |
| `completed_duplicate` | Work уже completed; handler пропускается. |
| `in_progress_duplicate` | Другое execution владеет key; handler пропускается. |
| `released` | Handler failed, claim released. |
| `completed` | Completion marker сохранен. |
| `complete_conflict` | Completion failed; это correctness signal. |

## TTL policy

`InProgressTtl` ограничивает, как долго abandoned claim блокирует work. Result TTL можно задавать per job или через global defaults и min/max policy.

TTL - business decision. Payment keys могут требовать более долгого retention, чем low-risk notifications.

## Архитектурное решение

### Почему этот pattern?

At-least-once delivery означает, что handlers могут выполняться больше одного раза. Middleware дает applications центральное место для защиты side effects, не встраивая duplicate claim logic в каждый handler.

### Trade-offs

Idempotency middleware зависит от корректного claim store. Если claim store недостаточно durable для защищаемого side effect, duplicates все еще могут пройти наружу.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Enqueue idempotency only | Просто. | Не защищает duplicate execution после retry/crash. |
| Handler-only manual dedupe | Flexible. | Повторяющийся boilerplate и inconsistent behavior. |
| Downstream provider idempotency only | Strong для supported APIs. | Не каждый side effect имеет provider keys. |

### Дополнительные вопросы

**Почему enqueue idempotency недостаточно?**  
Потому что один accepted job может выполниться дважды после crash или recovery.

**Что если handler succeeds, но completion marker fails?**  
Система может запустить handler снова. Downstream side effect все равно нуждается в собственном idempotency key там, где duplicates недопустимы.

**Почему release claim при handler failure?**  
Потому что retry должен иметь возможность снова попытаться выполнить work после transient failure.
