# Transaction Integrity

![Transaction integrity across Hot, Archive, and DLQ](/diagrams/22-transaction-integrity-hot-archive-dlq.png)

Transaction integrity - это граница между "job moved" и "job скопировали куда-то и, возможно, удалили позже". ChokaQ рассматривает final lifecycle transitions как database integrity boundaries.

Важные moves:

- `JobsHot` -> `JobsArchive` после success;
- `JobsHot` -> `JobsDLQ` после terminal failure, cancellation или zombie rescue;
- `JobsDLQ` -> `JobsHot` во время resurrection;
- final-state counter и metric bucket updates, которые должны совпадать с move.

## Где это находится

SQL templates живут в `ChokaQ.Storage.SqlServer.DataEngine.Queries`. Storage API выполняет их через `SqlJobStorage`. Worker-owned finalization проходит через `JobProcessor` и `JobStateManager`.

## Move pattern

Основной pattern:

1. `SET XACT_ABORT ON`.
2. Begin короткую SQL transaction.
3. Delete source row.
4. Capture exact deleted row через `OUTPUT ... INTO @Moved`.
5. Insert captured row в destination table.
6. Update `StatsSummary`.
7. Update `MetricBuckets`, когда move представляет completion outcome.
8. Commit.
9. Roll back on error.

Это означает, что job никогда не находится в half-moved состоянии. Если destination insert или counter update падает, SQL Server rollback'ит source row.

## Почему delete first?

Для Archive и DLQ moves удаление из `JobsHot` первым шагом захватывает authoritative row. Row, captured в `@Moved`, - ровно та row, которая была removed. Это важно, когда stale workers, zombie rescue, cancellation или admin actions race'ятся друг с другом.

Для resurrection удаление из `JobsDLQ` первым шагом не дает двум admin tabs вставить один и тот же job обратно в `JobsHot`.

## Ownership guards

Worker-owned transitions включают `WorkerId` predicates там, где это применимо. Stale worker не должен finaliz'ить job после того, как другой recovery path уже reclaim'нул или moved его.

Zero affected rows не считаются успешным movement. Они означают, что runtime больше не owns эту row.

## Что transactions не решают

Database transactions защищают состояние ChokaQ. Они не делают external side effects exactly-once.

Если handler списал карту, а process crash'нулся до Archive, ChokaQ может запустить job снова после recovery. Исправление - business idempotency на границе handler'а или downstream provider.

## Архитектурное решение

### Почему этот pattern?

Database уже является durable source of truth. Короткие transactions для lifecycle moves держат correctness локально внутри storage provider и не вводят вторую coordination system.

### Trade-offs

Transactions добавляют lock duration и требуют аккуратного query design. ChokaQ держит transactions короткими и ограничивает их state movement, counters и metric bucket writes. User handler code никогда не выполняется внутри этих SQL transactions.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Copy then delete in application code | Просто написать. | Может duplicate или lose rows при crash. |
| Single-table status update | Меньше cross-table moves. | Hot path конкурирует с historical data. |
| Distributed transaction with external side effect | Сильнее теоретическая coupling. | Operationally fragile и редко поддерживается modern services. |

### Когда не использовать этот подход

Для extremely high-throughput ephemeral events, где audit/history не важны, broker append log может подойти лучше. ChokaQ оптимизируется под durable business work, visibility и repairability.

### Дополнительные вопросы

**Какие operations должны быть atomic?**  
Final state transitions: Hot to Archive, Hot to DLQ, DLQ to Hot, и связанные counters/metric buckets.

**Что будет после handler success, но до Archive, если process crash'нется?**  
Job может быть recovered и выполнен снова. Поэтому ChokaQ документирует at-least-once execution и требует idempotent side effects.

**Почему не поместить handler execution внутрь SQL transaction?**  
Потому что external calls могут быть медленными, держать locks, timeout'иться или вообще не участвовать в database transaction. Transaction должна защищать состояние ChokaQ, а не оборачивать arbitrary business work.
