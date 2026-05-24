# SQL Concurrency: UPDLOCK + READPAST

![SQL locking with READPAST and UPDLOCK](/diagrams/21-sql-locking-readpast-updlock.png)

## Задача: competing consumers

Несколько worker instances или threads должны одновременно забирать задания из **одной таблицы** без:

- ситуации, где два воркера забрали **одно и то же задание**;
- взаимной блокировки воркеров: deadlocks, waits;
- **lost updates**, когда concurrent `UPDATE` сталкиваются друг с другом.

Это классическая задача **competing consumers** в distributed systems.

## Решение: один SQL query

```sql
-- From: Queries.cs — FetchNextBatch

WITH ActiveCounts AS (
    SELECT [Queue], COUNT(1) AS CurrentActive
    FROM [{SCHEMA}].[JobsHot]
    WHERE [Status] IN (1, 2)
    GROUP BY [Queue]
),
Candidates AS (
    SELECT
        h.[Id],
        h.[Priority],
        ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) AS SortUtc,
        q.[MaxWorkers],
        ISNULL(ac.CurrentActive, 0) AS CurrentActive,
        ROW_NUMBER() OVER (
            PARTITION BY h.[Queue]
            ORDER BY h.[Priority] DESC,
                     ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
        ) AS QueueRank
    FROM [{SCHEMA}].[JobsHot] h WITH (UPDLOCK, READPAST)
    LEFT JOIN [{SCHEMA}].[Queues] q ON h.[Queue] = q.[Name]
    LEFT JOIN ActiveCounts ac ON h.[Queue] = ac.[Queue]
    WHERE h.[Status] = 0
      AND (q.[IsPaused] IS NULL OR q.[IsPaused] = 0)
      AND (q.[IsActive] IS NULL OR q.[IsActive] = 1)
      AND (h.[ScheduledAtUtc] IS NULL OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())
),
Picked AS (
    SELECT TOP (@Limit) [Id]
    FROM Candidates
    WHERE [MaxWorkers] IS NULL
       OR ([CurrentActive] + [QueueRank]) <= [MaxWorkers]
    ORDER BY [Priority] DESC, [SortUtc] ASC
)
UPDATE h
SET h.[Status] = 1,                    -- Fetched
    h.[WorkerId] = @WorkerId,
    h.[LastUpdatedUtc] = SYSUTCDATETIME()
OUTPUT
    INSERTED.[Id], INSERTED.[Queue], INSERTED.[Type],
    INSERTED.[Payload], INSERTED.[Tags], INSERTED.[IdempotencyKey],
    INSERTED.[Priority], INSERTED.[Status], INSERTED.[AttemptCount],
    INSERTED.[WorkerId], INSERTED.[HeartbeatUtc],
    INSERTED.[ScheduledAtUtc], INSERTED.[CreatedAtUtc],
    INSERTED.[StartedAtUtc], INSERTED.[LastUpdatedUtc],
    INSERTED.[CreatedBy], INSERTED.[LastModifiedBy]
FROM [{SCHEMA}].[JobsHot] h WITH (UPDLOCK, READPAST)
INNER JOIN Picked p ON p.[Id] = h.[Id]
```

Разберем это по частям.

## Locking hints

### `UPDLOCK` - зарезервировать перед update

```sql
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, ...)
```

Когда SQL Server читает строку с `UPDLOCK`, он берет **update lock** (U-lock) на эту строку:

- другие readers, обычный `SELECT`, все еще могут читать строку;
- другие `UPDLOCK` readers **пропускают** строку или **ждут**;
- никакая другая transaction не может update/delete эту строку.

U-lock удерживается до завершения transaction. В нашем случае - до окончания `UPDATE SET Status = 1`.

### `READPAST` - пропускать locked rows

```sql
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, READPAST)
```

Без `READPAST` Worker B блокировался бы и ждал, пока Worker A отпустит locks. С `READPAST`:

```
Worker A: SELECT with UPDLOCK → grabs rows 1, 2, 3 (locks them)
Worker B: SELECT with UPDLOCK, READPAST → SKIPS 1,2,3 → grabs rows 4, 5, 6
Worker C: SELECT with UPDLOCK, READPAST → SKIPS 1-6 → grabs rows 7, 8, 9
```

Hot path не ждет строки, которые уже locked другим воркером. Это операционная причина использовать `READPAST`: воркеры продолжают движение, а не строятся convoy за первой locked row.

### Committed ActiveCounts

```sql
WITH ActiveCounts AS (
    SELECT [Queue], COUNT(1) AS CurrentActive
    FROM [chokaq].[JobsHot]
    WHERE [Status] IN (1, 2)
    GROUP BY [Queue]
)
```

CTE, который считает active jobs per queue, намеренно использует default committed-read behavior. Этот count участвует в bulkhead decision, поэтому dirty reads здесь недопустимы:

- dirty low count мог бы временно вывести queue за предел `MaxWorkers`;
- dirty high count мог бы starve'ить queue из-за работы, которая потом rollback'нется;
- capacity decisions относятся к correctness path; `NOLOCK` допустим только для passive dashboard telemetry.

Это все еще не serializable global semaphore. Основная защита остается в single `UPDATE ... OUTPUT` fetch statement и worker-owned gate `MarkAsProcessing`, но committed active counts убирают решения, основанные на uncommitted data.

## Pattern `UPDATE ... OUTPUT`

Вместо `SELECT` + `UPDATE`, то есть двух round-trips и окна для race condition, ChokaQ использует один `UPDATE...OUTPUT`:

```sql
UPDATE TOP (@Limit) h
SET h.[Status] = 1,
    h.[WorkerId] = @WorkerId,
    h.[LastUpdatedUtc] = SYSUTCDATETIME()
OUTPUT INSERTED.*                     -- Return the updated rows
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, READPAST)
WHERE ...
```

**Почему это сильнее, чем SELECT + UPDATE:**

| Approach | Round-trips | Race Window | Locks |
|----------|------------|-------------|-------|
| `SELECT` then `UPDATE` | 2 | Gap between SELECT and UPDATE | Нужно держать lock через две операции |
| `UPDATE...OUTPUT` | 1 | Нет, операция atomic | Lock берется и отпускается в одной операции |

## Candidate filters

```sql
WHERE h.[Status] = 0                                   -- Only Pending
  AND (q.[IsPaused] IS NULL OR q.[IsPaused] = 0)       -- Not paused
  AND (q.[IsActive] IS NULL OR q.[IsActive] = 1)       -- Not deactivated
  AND (h.[ScheduledAtUtc] IS NULL                      -- Not scheduled for future
       OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())

-- Bulkhead check happens after candidates are ranked per queue:
WHERE [MaxWorkers] IS NULL
   OR ([CurrentActive] + [QueueRank]) <= [MaxWorkers]
```

| # | Filter | Purpose |
|---|--------|---------|
| 1 | `Status = 0` | Забирать только Pending jobs, не Fetched и не Processing. |
| 2 | `IsPaused = 0` | Уважать queue pause. |
| 3 | `IsActive = 1` | Пропускать deactivated queues. |
| 4 | `ScheduledAtUtc <= NOW` | Fetch'ить только задания, чей delay истек. |
| 5 | `CurrentActive + QueueRank <= MaxWorkers` | Enforce remaining per-queue capacity внутри batch, чтобы один fetch не превысил queue limit. |

## ORDER BY: priority + schedule

```sql
ORDER BY h.[Priority] DESC,
         ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
```

1. **Priority DESC** - большее число выбирается раньше, если оба задания eligible.
2. **ScheduledAt ASC** - внутри одного priority более старая eligible work выбирается раньше новой.

`ISNULL` обрабатывает частый случай, когда `ScheduledAtUtc` равен NULL, то есть immediate execution, и использует creation time для порядка выбора.

Это fetch ordering policy, а не строгая end-to-end FIFO guarantee. Multiple workers, per-queue limits, retries, pause/resume, cancellation и operator actions могут менять completion order.

## Fairness policy

SQL fetch дает best-effort scheduling, а не strict global fairness. Каждый fetch claim сортирует eligible rows по priority и due time, а per-queue `MaxWorkers` не дает одной queue потребить больше настроенной capacity. Под высокой concurrency `READPAST` может пропустить rows, locked другим воркером, поэтому более поздняя eligible row может быть claim'нута раньше.

Этот trade-off намеренный: ChokaQ предпочитает forward progress и duplicate claim prevention вместо ожидания за locked rows. Если нужна более сильная business fairness, кодируйте ее через separate queues, explicit priorities и `MaxWorkers` caps.

## Concurrency proof

**Сценарий:** 3 воркера, batch size 2, 9 pending jobs

```
Database state BEFORE fetch:

| Row 1 | Row 2 | Row 3 |
|---|---|---|
| Job-1 (P) | Job-2 (P) | Job-3 (P) |
| Job-4 (P) | Job-5 (P) | Job-6 (P) |
| Job-7 (P) | Job-8 (P) | Job-9 (P) |


Worker A executes: UPDATE TOP(2) ... WITH (UPDLOCK, READPAST)
  → Locks Job-1, Job-2
  → Sets Status=1, WorkerId="worker-A"
  → OUTPUT returns Job-1, Job-2

Worker B executes: UPDATE TOP(2) ... WITH (UPDLOCK, READPAST)
  → READPAST skips Job-1, Job-2 (locked by A)
  → Locks Job-3, Job-4
  → Sets Status=1, WorkerId="worker-B"
  → OUTPUT returns Job-3, Job-4

Worker C executes: UPDATE TOP(2) ... WITH (UPDLOCK, READPAST)
  → READPAST skips Job-1–4 (locked by A, B)
  → Locks Job-5, Job-6
  → OUTPUT returns Job-5, Job-6

Result: each worker gets a unique pair from this fetch path.
```

::: tip 💡 Архитектурная деталь
Комбинация `UPDLOCK + READPAST` дает ChokaQ database-level claim primitive. Она предотвращает duplicate fetch claims без distributed lock service. Processing все равно использует worker ownership и lease checks, потому что реальные системы должны также переживать shutdown, stale buffers, zombie rescue и operator actions после initial fetch.
:::

<br>

> *Дальше: [Expression Trees](/ru/3-deep-dives/expression-trees) показывают, как убрать reflection overhead при вызове handler'а.*

## Архитектурное решение

### Почему этот pattern?

SQL является coordination boundary. `UPDLOCK` резервирует candidate rows перед update, а `READPAST` позволяет другим воркерам пропускать locked rows вместо blocking.

### Trade-offs

`READPAST` может временно пропускать locked rows, поэтому strict FIFO под contention не гарантируется. ChokaQ выбирает progress и concurrency вместо perfect ordering.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Distributed lock service | Explicit locks. | Дополнительная infrastructure и failure modes. |
| `SELECT` then `UPDATE` | Просто. | Races при concurrent workers. |
| Broker visibility timeout | Mature primitive. | Другая operational model. |

### Дополнительные вопросы

**Почему `UPDATE ... OUTPUT`?**  
Чтобы claim'ить и вернуть exact rows одной database operation.

**В чем downside `READPAST`?**  
Он обменивает strict ordering на throughput, пропуская locked rows.

**Где duplicate execution все еще возможен?**  
После side effects handler'а, но до finalization. Эту boundary закрывает handler idempotency.
