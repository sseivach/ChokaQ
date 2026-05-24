# CHK-03: Bulkhead Isolation

## Проблема Noisy Neighbor

Представьте две очереди в системе:

| Queue | Task | Duration | Volume |
|-------|------|----------|--------|
| `pdf-generation` | Генерация отчетов на 200 страниц | 30-120 секунд | 50/hour |
| `sms-notifications` | Отправка SMS через API | 200ms | 10,000/hour |

Без изоляции burst PDF-заданий может занять доступные execution slots. SMS-уведомления начнут ждать за workload'ом с совершенно другой длительностью и срочностью.

Это проблема **Noisy Neighbor**: одна resource-intensive нагрузка влияет на latency несвязанной работы.

## Решение ChokaQ: database-level bulkheads

ChokaQ решает это на уровне **SQL query** - внутри CTE `FetchNextBatch`:

```sql
-- From: Queries.cs — FetchNextBatch

WITH ActiveCounts AS (
    -- Count committed active jobs per queue. This is part of capacity control,
    -- so it must not use dirty reads.
    SELECT [Queue], COUNT(1) AS CurrentActive
    FROM [chokaq].[JobsHot]
    WHERE [Status] IN (1, 2)    -- Fetched + Processing
    GROUP BY [Queue]
)
SELECT TOP (@Limit) h.*
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, READPAST)
LEFT JOIN [chokaq].[Queues] q ON h.[Queue] = q.[Name]
LEFT JOIN ActiveCounts ac ON h.[Queue] = ac.[Queue]
WHERE h.[Status] = 0
  AND (q.[IsPaused] IS NULL OR q.[IsPaused] = 0)
  AND (q.[IsActive] IS NULL OR q.[IsActive] = 1)
  -- THE BULKHEAD: simple form shown for readability.
  -- Production SQL ranks candidates per queue and applies:
  -- CurrentActive + QueueRank <= MaxWorkers
  AND (q.[MaxWorkers] IS NULL OR ISNULL(ac.CurrentActive, 0) < q.[MaxWorkers])
  AND (h.[ScheduledAtUtc] IS NULL OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())
ORDER BY h.[Priority] DESC,
         ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
```

### Как это работает

В таблице `Queues` есть колонка `MaxWorkers`:

```sql
CREATE TABLE [chokaq].[Queues](
    [Name]                 VARCHAR(255) NOT NULL,
    [IsPaused]             BIT NOT NULL DEFAULT 0,
    [IsActive]             BIT NOT NULL DEFAULT 1,
    [ZombieTimeoutSeconds] INT NULL,
    [MaxWorkers]           INT NULL,    -- 👈 Bulkhead limit
    [LastUpdatedUtc]       DATETIME2(7) NOT NULL
);
```

**Пример configuration:**

| Queue | MaxWorkers | Effect |
|-------|-----------|--------|
| `pdf-generation` | `3` | Максимум 3 PDF одновременно в processing |
| `sms-notifications` | `NULL` | Без лимита - использует всю доступную capacity |
| `email-sending` | `10` | Не больше 10 concurrent email sends |

### Visualization

![Queue bulkhead isolation](/diagrams/40-queue-bulkhead-isolation.png)

Когда PDF-очередь ограничена тремя active jobs, SMS work все еще может быть claim'нута при наличии worker capacity, а не ждать за всем PDF backlog.

## Почему database-level?

Bulkheads можно enforcing'ить в нескольких местах. Process-local semaphores легко понимать внутри одного host. ChokaQ применяет этот лимит в storage claim path, потому что SQL database общая для всех worker instances:

| Approach | Trade-off |
|----------|---------|
| **Process-local** (thread pools или semaphores) | Просто внутри одного host, но каждый instance владеет собственным лимитом. |
| **Database-level** (ChokaQ) | Немного больше SQL work во время fetch, зато все instances видят одни и те же active counts. |

::: tip 💡 Архитектурная деталь
Enforcing Bulkhead pattern на уровне database дает каждому воркеру одинаковый committed view active work. Production SQL также ранжирует candidates per queue, что не дает одному fetch batch claim'нуть больше строк, чем оставшаяся capacity очереди.
:::

::: tip 💡 Scale trade-off: почему database-level?
Можно спросить: "Разве `COUNT(1)` на database для каждого fetch не дорогой?" Стоимость зависит от формы таблицы. Благодаря архитектуре **Three Pillars** `JobsHot` содержит только active lifecycle rows. Поэтому active-count reads остаются ограничены текущей работой, а не retained history.

Trade-off намеренный: потратить небольшой объем SQL work, чтобы держать cluster-wide queue capacity в той же consistency boundary, что и job claiming.
:::

## Runtime adjustment

Лимиты можно менять **во время работы** через The Deck dashboard или API - restart не нужен:

```csharp
// Via IJobStorage
await _storage.SetQueueMaxWorkersAsync("pdf-generation", maxWorkers: 5);

// Or set to NULL to remove the limit
await _storage.SetQueueMaxWorkersAsync("pdf-generation", maxWorkers: null);
```

Изменение вступает в силу на **следующем fetch cycle** в пределах `PollingInterval` seconds.

## Queue controls beyond bulkhead

Таблица `Queues` дает дополнительные controls:

| Control | Column | Effect |
|---------|--------|--------|
| **Pause** | `IsPaused = 1` | Задания очереди пропускаются во время fetch. Уже выполняющиеся задания продолжаются. |
| **Deactivate** | `IsActive = 0` | Очередь полностью отключена - задания release'ятся обратно в Pending |
| **Zombie Timeout** | `ZombieTimeoutSeconds` | Per-queue heartbeat threshold, override для global setting |
| **Bulkhead** | `MaxWorkers` | Максимальное число concurrent processing slots для очереди |

Все четыре control доступны в The Deck dashboard и применяются без restart или redeployment.

## Архитектурное решение

ChokaQ enforcing'ит queue bulkheads в storage claim path, а не только внутри одного worker process. Это важно, потому что production deployments часто запускают несколько application instances. Local semaphore может ограничить каждый process, но не может ограничить весь fleet без координации через другую shared system.

SQL fetch query уже принимает решение claim'ить работу, поэтому это правильное место для global queue capacity. `MaxWorkers` становится shared production contract: если queue ограничена тремя active jobs, "три" означает три на всех hosts, которые используют одну database.

Trade-off - дополнительная SQL work во время fetch. ChokaQ принимает эту стоимость, потому что Hot table намеренно маленькая и индексирована под active work. Результат - более сильная isolation при меньшем числе moving parts.

## Дополнительные вопросы

**Почему database-level bulkhead лучше per-process worker pools?**  
Потому что database видит все active rows со всех instances. Per-process pools умножают capacity на instance count и дрейфуют при каждом autoscaling change.

**Может ли bulkhead вызвать starvation очереди?**  
Очередь может быть намеренно ограничена, но не должна starve unrelated queues. Если лимит слишком низкий, растет ее собственный lag, и The Deck показывает это, чтобы operators могли поднять `MaxWorkers` или разделить workload.

**Какой риск у подсчета active jobs в fetch query?**  
Риск - database overhead. Mitigation - модель Three Pillars: `JobsHot` хранит только active lifecycle rows, поэтому active-count queries ограничены current work, а не archive history.

<br>

> *Дальше: как [ZombieRescueService](/ru/2-lifecycle/zombie-rescue) автоматически восстанавливает crashed jobs.*
