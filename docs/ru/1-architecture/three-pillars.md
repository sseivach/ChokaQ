# CHK-01: Архитектура Three Pillars

![Three Pillars enterprise map](/diagrams/20-three-pillars-enterprise.png)

## Базовая проблема

Background job store может моделировать весь lifecycle в одной таблице:
pending, processing, succeeded, failed, cancelled и delayed rows находятся в
одной физической структуре. Это разумная стартовая точка, потому что schema
простая, но по мере роста active work и long-lived history такая модель создает
давление:

```text
| Id | Status    | Payload | CreatedAt | ... |
|----|-----------|---------|-----------|-----|
| 1  | Succeeded | ...     | Jan 1     |     |  ← Completed 6 months ago
| 2  | Succeeded | ...     | Jan 2     |     |  ← Completed 6 months ago
| .. | ...       | ...     | ...       |     |  ← 2 million more rows
| N  | Pending   | ...     | Today     |     |  ← Active work the worker needs now
```

Fetch path интересуют eligible active rows. Historical rows решают другую
задачу: audit, investigation, retention и reporting. Когда обе формы данных
живут вместе, active fetch path постоянно фильтрует данные, которые ему не
нужны. Со временем:

- растет index fragmentation из-за постоянных `INSERT`/`DELETE` в одном B-tree;
- page splits замедляют writes;
- `COUNT(*)` для dashboard stats становится дорогим.

## Решение: физическое разделение данных

ChokaQ делит данные на **три физически отдельные таблицы**, каждая из которых
оптимизирована под свой workload:

![Three Pillars architecture](/diagrams/20-three-pillars-enterprise.png)

### Pillar 1: JobsHot

High-concurrency рабочая таблица. Содержит только jobs, которые **активно
участвуют в выполнении**.

| Column | Purpose |
|--------|---------|
| `Status` | `0:Pending`, `1:Fetched`, `2:Processing` - только три состояния |
| `Priority` | Descending sort: большее число выполняется раньше |
| `HeartbeatUtc` | Обновляется каждые N секунд во время processing, используется для zombie detection |
| `ScheduledAtUtc` | Delayed/retry scheduling; `NULL` означает "run now" |
| `IdempotencyKey` | Unique filtered index для защиты от duplicate enqueuing |

**Ключевая оптимизация:** fetch index является **filtered** и содержит только
`Pending` jobs:

```sql
CREATE NONCLUSTERED INDEX [IX_JobsHot_Fetch]
ON [chokaq].[JobsHot] ([Queue], [Priority] DESC, [ScheduledAtUtc], [CreatedAtUtc])
INCLUDE ([Id], [Type])
WHERE [Status] = 0                    -- 👈 Only Pending jobs in the index
WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
```

Это держит fetch index сфокусированным на eligible work. Даже если тысячи jobs
processing или уже сохранены в других таблицах, fetch index отслеживает только
pending rows. `CreatedAtUtc` входит в key, потому что delayed jobs и immediate
jobs используют один fetch path: engine сортирует по effective schedule time и
использует creation time как стабильный tie-breaker.

### Pillar 2: JobsArchive

Write-once, read-many. После успешного завершения job атомарно переносится сюда.

| Column | Purpose |
|--------|---------|
| `DurationMs` | Execution time для performance analytics |
| `FinishedAtUtc` | Completion timestamp для trend charts |
| `AttemptCount` | Сколько попыток потребовалось |

**Ключевая оптимизация:** `PAGE` compression, потому что данные редко
обновляются:

```sql
CONSTRAINT [PK_JobsArchive] PRIMARY KEY CLUSTERED ([Id] ASC)
WITH (DATA_COMPRESSION = PAGE)
```

SQL Server PAGE compression может дать **50-70% storage reduction** на
text-heavy rows.

### Pillar 3: JobsDLQ

Jobs, которые достигли terminal failure state и требуют inspection, repair или
explicit cleanup. Каждая row хранит failure reason и diagnostic details:

```csharp
public enum FailureReason
{
    MaxRetriesExceeded = 0,  // Exhausted all retry attempts
    Cancelled = 1,           // Admin cancelled via The Deck
    Zombie = 2,              // Heartbeat expired — worker crashed
    CircuitBreakerOpen = 3,  // Too many failures for this job type
    Rejected = 4,            // Validation failure on enqueue
    Throttled = 5,           // Downstream rate limit or overload signal
    FatalError = 6,          // Poison-pill failure that should not retry
    Timeout = 7,             // Handler exceeded its execution timeout
    Transient = 8            // Retryable failure family after exhaustion
}
```

DLQ поддерживает:

- **Filtering by reason**: например, "показать zombies за прошлую неделю";
- **Payload editing**: исправление broken JSON прямо в The Deck;
- **Resurrection**: перенос обратно в Hot table со сброшенным `AttemptCount`.

### Pillar 4: StatsSummary

Pre-aggregated counters для **O(1) dashboard reads**:

```sql
CREATE TABLE [chokaq].[StatsSummary](
    [Queue]           VARCHAR(255) NOT NULL,
    [SucceededTotal]  BIGINT NOT NULL DEFAULT 0,
    [FailedTotal]     BIGINT NOT NULL DEFAULT 0,
    [RetriedTotal]    BIGINT NOT NULL DEFAULT 0,
    [LastActivityUtc] DATETIME2(7) NULL
);
```

Вместо подсчета retained history при каждом dashboard refresh The Deck читает
одну pre-computed row.

### Pillar 5: MetricBuckets

`StatsSummary` отвечает на lifetime counters. `MetricBuckets` отвечает на
recent-rate questions:

- jobs processed per second в rolling dashboard windows;
- failed vs processed percentage;
- duration aggregates для будущих charts;
- outcome history, которая не исчезает при requeue или purge DLQ row.

Это осознанная эволюция от более раннего bounded-lookback design. Читать recent
Archive/DLQ rows было просто и полезно, но это связывало dashboard rate queries
с lifecycle history и заставляло operator cleanup переписывать recent failure
windows. `MetricBuckets` записывает completion outcome в той же transaction,
что и Hot -> Archive или Hot -> DLQ, а The Deck читает маленький диапазон
recent buckets.

Полный разбор trade-off: [Rolling Observability Buckets](/ru/4-the-deck/rolling-observability).

## Atomic transitions: safety net

Каждое перемещение между pillars является **atomic**: один SQL batch либо
полностью succeeds, либо полностью fails.

### Success path: Hot -> Archive

```sql
-- Delete from Hot and capture the row via OUTPUT
DELETE FROM [chokaq].[JobsHot]
OUTPUT
    DELETED.[Id], DELETED.[Queue], DELETED.[Type], DELETED.[Payload],
    DELETED.[Tags], DELETED.[AttemptCount], DELETED.[WorkerId],
    DELETED.[CreatedBy], NULL,
    DELETED.[CreatedAtUtc], DELETED.[StartedAtUtc],
    SYSUTCDATETIME(), @DurationMs
INTO [chokaq].[JobsArchive](...)
WHERE [Id] = @JobId;

-- Atomically increment the success counter
MERGE [chokaq].[StatsSummary] AS target
USING (SELECT @Queue AS Queue) AS source
ON target.[Queue] = source.[Queue]
WHEN MATCHED THEN
    UPDATE SET SucceededTotal = SucceededTotal + 1,
               LastActivityUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (Queue, SucceededTotal, ...) VALUES (@Queue, 1, ...);
```

::: danger Critical Design Decision
Важный pattern не в том, чтобы "скопировать в application code, а потом удалить".
ChokaQ выполняет move внутри одной SQL transaction и использует `OUTPUT`, чтобы
захватить точную row, которая перемещается. Это сохраняет state transition
atomic и дает worker-ownership guards одну точку, где можно решить, остается ли
move валидным.
:::

### Failure path: Hot -> DLQ

Тот же pattern, но с `FailureReason` и `ErrorDetails`:

```sql
DELETE FROM [chokaq].[JobsHot]
OUTPUT
    DELETED.[Id], DELETED.[Queue], DELETED.[Type], DELETED.[Payload],
    DELETED.[Tags], @FailureReason, @ErrorDetails, DELETED.[AttemptCount],
    DELETED.[WorkerId], DELETED.[CreatedBy], NULL,
    DELETED.[CreatedAtUtc], SYSUTCDATETIME()
INTO [chokaq].[JobsDLQ](...)
WHERE [Id] = @JobId;
```

### Resurrection path: DLQ -> Hot

Обратное движение: job получает второй шанс со сброшенным состоянием.

```sql
DELETE FROM [chokaq].[JobsDLQ]
OUTPUT
    DELETED.[Id], DELETED.[Queue], DELETED.[Type],
    CASE WHEN @NewPayload IS NOT NULL THEN @NewPayload ELSE DELETED.[Payload] END,
    CASE WHEN @NewTags IS NOT NULL THEN @NewTags ELSE DELETED.[Tags] END,
    NULL,   -- IdempotencyKey reset
    ISNULL(@NewPriority, 10),
    0,      -- Status = Pending
    0,      -- AttemptCount reset to 0
    ...
INTO [chokaq].[JobsHot](...)
WHERE [Id] = @JobId;
```

::: tip Architecture Insight
Обновление только status column сохраняет schema меньшей, но fetch query всегда
должен фильтровать completed rows. При физическом разделении fetch index
содержит только active jobs, поэтому hot path остается сфокусированным и
предсказуемым.
:::

## Performance impact

| Metric | Single-table design | Three Pillars |
|--------|-------------------|---------------|
| Fetch query scan | Все rows, включая миллионы historical rows | Только Pending rows |
| Index fragmentation | Высокая: mixed INSERT/DELETE | Ниже: append-mostly по разным tables |
| Archive query speed | Конкурирует с active queries | Dedicated index, PAGE compressed |
| Dashboard stats | `COUNT(*)` full scan | O(1) read from StatsSummary |
| Storage efficiency | Одна storage policy для mixed lifecycle rows | PAGE compression на cold data |

<br>

> Дальше: [Почему SQL Server?](/ru/1-architecture/why-sql-server) - database-level features, которые делают эту архитектуру возможной.

## Архитектурное решение

### Почему выбран такой pattern?

Active work, successful history и failed recovery work имеют разные query
patterns. Физическое разделение держит worker fetch маленьким, но сохраняет
audit и repair data.

### Trade-offs

Модель требует atomic cross-table moves. ChokaQ принимает эту сложность и
использует короткие SQL transactions, чтобы rows не оставались half-moved.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| One jobs table | Simple schema. | Active fetch competes with years of history. |
| Broker plus separate history store | High broker throughput. | Split operational state. |
| No DLQ | Smaller model. | Failed work has no repair workflow. |

### Дополнительные вопросы

**Почему не одна jobs table?**  
Потому что active fetch не должен деградировать по мере роста history.

**Что делает разделение безопасным?**  
Atomic state transitions через SQL transactions и ownership predicates.

**Главная цена?**  
Больше schema и transition logic, которые нужно тестировать и документировать.
