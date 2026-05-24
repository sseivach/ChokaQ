# Retention Cleanup

![Retention cleanup](/diagrams/51-retention-cleanup.png)

Retention cleanup не дает history tables расти бесконечно, сохраняя достаточно evidence для operations, audit и incident response.

Cleanup намеренно отделен от worker execution. Active work должен оставаться fast и predictable.

## What can be cleaned

| Data | Cleanup meaning |
|---|---|
| `JobsArchive` | Remove old successful history after retention window. |
| `JobsDLQ` | Remove failed/recovery evidence only after explicit retention policy. |
| `MetricBuckets` | Remove old rolling observability buckets after they are no longer useful. |

`JobsHot` - active work, и retention policy не должна его чистить.

## Batch size

`SqlServer.CleanupBatchSize` ограничивает cleanup delete operations. Large one-shot deletes могут держать locks, раздувать transaction log и мешать dashboard queries.

Batch cleanup дает operators predictable pressure envelope.

## DLQ retention is different

DLQ rows - не просто old data. Это recovery evidence. Слишком ранний purge DLQ может уничтожить единственную копию failed payload и error reason.

Production teams должны определить:

- minimum DLQ retention;
- кто может purge;
- требует ли bulk purge approval;
- содержат ли DLQ payloads sensitive data;
- нужно ли export failed payloads before deletion.

## Архитектурное решение

### Почему этот pattern?

Retention - operational policy, а не worker correctness decision. Batched и explicit cleanup не превращает normal processing в accidental history management.

### Trade-offs

Long retention увеличивает storage cost и dashboard history volume. Short retention уменьшает investigation ability.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Keep everything forever | Maximum evidence. | Unbounded storage и compliance risk. |
| Purge aggressively | Small database. | Weak incident analysis и recovery. |
| Archive externally | Keeps SQL smaller. | Adds data pipeline и split investigation surface. |

### Дополнительные вопросы

**Почему batch cleanup?**  
Чтобы bound locks, log growth и operational impact.

**Почему DLQ отличается от Archive?**  
DLQ может содержать unresolved business work и recovery evidence.

**Cleanup должен трогать active jobs?**  
Нет. Active lifecycle policy и retention policy separate.
