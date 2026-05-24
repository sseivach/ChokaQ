# Edit + Resurrect (DLQ Management)

## Проблема: dead jobs с исправимыми ошибками

Job падает из-за malformed JSON payload:

```json
{
  "to": "user@example.com",
  "subject": "Welcome!"
  "body": "Hello, World!"     // ← Missing comma after "subject"
}
```

Без operator workflow команды часто уходят в manual recovery:

1. fix code или data, затем manually enqueue replacement work;
2. написать ad hoc SQL script для update failed row;
3. оставить failed work unresolved, пока support process не обработает ее.

Эти paths могут быть valid в emergencies, но их сложно audit'ить и легко применять inconsistent.

## Решение ChokaQ: Edit + Resurrect

The Deck дает **in-browser editing** DLQ job data и one-click resurrection.

![DLQ edit and resurrect](/diagrams/30-dlq-edit-resurrect.png)

### Workflow

```text
1. Job fails → lands in DLQ with FailureReason + ErrorDetails
2. Admin opens The Deck → navigates to DLQ panel
3. Clicks on the failed job → Inspector opens
4. Sees the full exception stack trace
5. Edits the JSON payload directly in the browser
6. Clicks "Resurrect" → job moves back to JobsHot with fixed payload
7. Job processes successfully on the next fetch cycle
```

## DLQ Inspector

Inspector panel показывает complete job details:

| Field | Example |
|-------|---------|
| **Job ID** | `01JARX7K8F...` |
| **Type** | `email_v1` |
| **Queue** | `notifications` |
| **Failure Reason** | `MaxRetriesExceeded` |
| **Attempts** | `3` |
| **Failed At** | `2026-04-30 14:32:07 UTC` |
| **Worker ID** | `worker-prod-01` |
| **Error Details** | Full exception with stack trace |
| **Payload** | Editable JSON |
| **Tags** | Editable metadata |

## Editing dead jobs

Два уровня editing:

### 1. Edit in DLQ без resurrection

Fix data для later analysis или batch resurrection:

```csharp
await _storage.UpdateDLQJobDataAsync(
    jobId: "job-123",
    updates: new JobDataUpdateDto
    {
        Payload = fixedJson,
        Tags = "reviewed,fixed-payload"
    },
    modifiedBy: "admin@company.com"
);
```

Job остается в DLQ, но уже с corrected data.

### 2. Edit + Resurrect: fix and retry

Fix payload AND move back to Hot table одной operation:

```csharp
await _storage.ResurrectAsync(
    jobId: "job-123",
    updates: new JobDataUpdateDto
    {
        Payload = fixedJson,          // Fixed JSON
        Tags = "resurrected,manual",  // Audit trail
        Priority = 30                 // Boost priority
    },
    resurrectedBy: "admin@company.com"
);
```

### Что происходит при resurrection

SQL atomically:

1. **Deletes** job из `JobsDLQ`.
2. **Inserts** into `JobsHot` with:
   - `Status = 0 (Pending)` - back in queue;
   - `AttemptCount = 0` - fresh start;
   - Updated Payload/Tags/Priority, если provided;
   - `LastModifiedBy = "admin@company.com"` - audit trail.
3. **Decrements** `StatsSummary.FailedTotal` - stats stay accurate.

```sql
DELETE FROM [chokaq].[JobsDLQ]
OUTPUT
    DELETED.[Id], DELETED.[Queue], DELETED.[Type],
    CASE WHEN @NewPayload IS NOT NULL
         THEN @NewPayload ELSE DELETED.[Payload] END,
    CASE WHEN @NewTags IS NOT NULL
         THEN @NewTags ELSE DELETED.[Tags] END,
    NULL,                              -- IdempotencyKey cleared
    ISNULL(@NewPriority, 10),          -- New or default priority
    0,                                 -- Status = Pending
    0,                                 -- AttemptCount = 0 (fresh)
    NULL, NULL,                        -- WorkerId, HeartbeatUtc
    NULL, DELETED.[CreatedAtUtc], NULL,
    SYSUTCDATETIME(),
    DELETED.[CreatedBy], @ResurrectedBy
INTO [chokaq].[JobsHot](...)
WHERE [Id] = @JobId;
```

## Bulk operations

### Bulk Resurrect

Resurrect multiple jobs at once, например "retry all zombies from yesterday":

```csharp
var zombieIds = dlqJobs
    .Where(j => j.FailureReason == FailureReason.Zombie)
    .Select(j => j.Id)
    .ToArray();

int resurrected = await _storage.ResurrectBatchAsync(
    jobIds: zombieIds,
    resurrectedBy: "admin@company.com"
);
// Processed in batches of 1000 for transaction safety
```

### Bulk Cancel

Cancel pending jobs, которые больше не нужны:

```csharp
int cancelled = await _storage.ArchiveCancelledBatchAsync(
    jobIds: selectedIds,
    cancelledBy: "admin@company.com"
);
```

### Purge

Permanently delete jobs from DLQ, irreversible:

```csharp
await _storage.PurgeDLQAsync(jobIds);
```

Или purge old archive entries:

```csharp
int deleted = await _storage.PurgeArchiveAsync(
    olderThan: DateTime.UtcNow.AddDays(-90)
);
```

SQL Server cleanup удаляет rows bounded transactions под контролем `SqlServer.CleanupBatchSize` (default: 1000). Operators все равно вызывают один purge method, но storage под ним повторяет short database commits, чтобы retention work не monopolize locks или transaction log space.

## Safety gates

### Hot-edit safety

Editing active jobs в Hot table restricted:

```csharp
// UpdateJobDataAsync — only works for Pending jobs
public async ValueTask<bool> UpdateJobDataAsync(
    string jobId, JobDataUpdateDto updates, ...)
{
    // WHERE Status = 0 — Safety gate!
    // Cannot edit Fetched or Processing jobs
}
```

Если job уже processing (`Status = 1 or 2`), edit возвращает `false`. Это предотвращает corruption in-flight work.

### DLQ edit safety

DLQ edits не имеют status restriction: job уже dead. Но все edits **audited** через `LastModifiedBy`.

::: tip 💡 Design Decision
Когда job resurrected, его `AttemptCount` reset'ится в 0, потому что root cause, вероятно, исправлена: payload corrected, external service restored. Это дает job полный fresh retry budget. Если fix не сработал, Smart Worker classify error и отправит job обратно в DLQ.
:::

## Common use cases

| Scenario | Action |
|----------|--------|
| Malformed JSON payload | Edit payload in DLQ -> Resurrect |
| External API was down | Wait for recovery -> Bulk resurrect all `MaxRetriesExceeded` |
| Code bug fixed and deployed | Bulk resurrect all `Fatal` jobs of that type |
| Zombie from server restart | Resurrect if idempotent, Purge if not |
| Cancelled by mistake | Resurrect with original data |
| Old archive cleanup | `PurgeArchiveAsync(olderThan: 90 days)` |

## Архитектурное решение

Edit and resurrect существует потому, что production failures не всегда исправляются restart workers. Иногда handler correct, а payload wrong; иногда dependency outage завершился, и dead work нужен controlled second chance. Заставлять operators писать ad hoc SQL для такого workflow рискованно и плохо audit'ится.

ChokaQ делает resurrection first-class storage operation. Он move'ит DLQ row обратно в Hot одной database operation, reset'ит retry state, records who made the change и дает normal worker path обработать job снова. Так recovery остается в той же lifecycle model, а не создает parallel manual re-enqueue path.

Trade-off - power. Editing payloads - опасная capability, и она должна быть protected authorization, audit logging и operational discipline. The Deck должен делать workflow easy, но не casual.

## Дополнительные вопросы

**Почему reset `AttemptCount` during resurrection?**  
Resurrection подразумевает, что operator изменил conditions: payload fixed, code deployed или dependency recovered. Fresh retry budget дает normal policy оценить repaired job.

**Почему не update DLQ row in place и process it from DLQ?**  
Потому что DLQ - terminal inspection table. Move row back to Hot восстанавливает standard execution path: fetch, processing, heartbeat, retry, archive и metrics.

**Что предотвращает unsafe editing of in-flight jobs?**  
Hot edits ограничены Pending jobs. Fetched и Processing jobs принадлежат workers, поэтому editing corrupt'ил бы active execution.

> *Фаза The Deck завершена. Для начала см. [Быстрый старт](/ru/getting-started) или [Architecture](/ru/1-architecture/three-pillars).*
