# Deduplication Layer

![Deduplication layer](/diagrams/58-deduplication-layer.png)

Deduplication layer - легкий active-key guard. Он отделен от durable enqueue idempotency и от handler idempotency middleware.

Его роль - подавлять short-lived duplicate actions: double-clicks, retry storms или repeated local attempts до того, как они дойдут до более тяжелой boundary.

## Где это находится

| Runtime type | Role |
|---|---|
| `IDeduplicator` | Internal contract для time-limited key acquisition. |
| `InMemoryDeduplicator` | Process-local implementation через `ConcurrentDictionary` и timer cleanup. |

## Behavior

`TryAcquireAsync(key, ttl)` возвращает:

- `true`, если key новый или expired;
- `false`, если key уже active.

Expired keys удаляются background cleanup timer.

## Correctness boundary

In-memory deduplicator является process-local. Он не дает durable или cross-host exactly-once behavior. Не используйте его как замену SQL idempotency keys или handler idempotency.

## Архитектурное решение

### Почему этот pattern?

Некоторое duplicate suppression полезно еще до storage или expensive runtime paths. Маленького in-memory guard достаточно для local burst suppression без добавления infrastructure.

### Trade-offs

Process-local deduplication не работает across multiple hosts и исчезает при restart. Это приемлемо только для soft suppression, не для correctness.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Durable SQL dedupe for everything | Сильнее. | Больше database writes для soft duplicate suppression. |
| Distributed cache lock | Cross-host. | Extra infrastructure и failure modes. |
| No dedupe layer | Проще. | Больше repeated local work под bursts. |

### Дополнительные вопросы

**Это exactly-once protection?**  
Нет. Это soft process-local duplicate guard.

**Что должно защищать business side effects?**  
Handler idempotency и downstream/provider idempotency.

**Зачем этот layer вообще нужен?**  
Чтобы дешево уменьшать repeated local actions до того, как они станут более тяжелой storage или execution work.
