# CHK-02: Машина состояний задания

## Путь задания

Каждое задание в ChokaQ проходит детерминированный путь через набор состояний. Понимание этого пути - ключ к пониманию всей системы.

## Состояния

| Status | Value | Location | Meaning |
|--------|-------|----------|---------|
| **Pending** | `0` | JobsHot | Ожидает в очереди и готово к выборке |
| **Fetched** | `1` | JobsHot | Загружено в буфер памяти воркера и ждет свободный execution slot |
| **Processing** | `2` | JobsHot | Активно выполняется, heartbeat обновляется |
| **Succeeded** | `3` | JobsArchive | Успешно завершено и перемещено в archive |
| **Failed** | `4` | JobsDLQ | Завершилось постоянной ошибкой и перемещено в DLQ для разбора |
| **Cancelled** | `5` | JobsDLQ | Отменено администратором и сохранено в DLQ/history workflow |
| **Zombie** | `6` | JobsDLQ | Воркер упал, heartbeat истек |

::: info 📝 Примечание
В режиме SQL Server статусы 3-6 являются **виртуальными**. У задания фактически нет колонки `Status` со значением 3: строка просто перемещается в таблицу `JobsArchive`. Эти enum-значения нужны для SignalR-уведомлений и In-Memory storage mode.
:::

## Полный lifecycle

![Job state machine](/diagrams/11-job-state-machine-full.png)

## Фаза 1: Enqueue

Когда вызывается `IChokaQQueue.EnqueueAsync()`:

```csharp
await _queue.EnqueueAsync(
    new SendEmailJob("user@example.com", "Welcome!"),
    priority: 20,              // Higher = processed first
    delay: TimeSpan.FromMinutes(5),  // Optional delayed execution
    idempotencyKey: "email-user-123" // Optional duplicate prevention
);
```

**Что происходит внутри:**

1. Генерируется уникальный ID в формате `ULID`, удобном для сортировки по времени.
2. Job DTO сериализуется в JSON.
3. **Idempotency check:** если ключ уже существует, возвращается существующий job ID, вставка пропускается.
4. В `JobsHot` вставляется строка со `Status = 0 (Pending)`.
5. Если задан delay, заполняется `ScheduledAtUtc`.
6. Записывается метрика `chokaq.jobs.enqueued`.
7. Через SignalR отправляется уведомление.

### Idempotency Guard

```sql
-- Check BEFORE insert to avoid unique constraint violations
SELECT [Id] FROM [chokaq].[JobsHot]
WHERE [IdempotencyKey] = @Key;

-- If found → return existing ID, done
-- If not → proceed with INSERT
```

Плюс уникальный filtered index как страховка:

```sql
CREATE UNIQUE NONCLUSTERED INDEX [IX_JobsHot_Idempotency]
ON [chokaq].[JobsHot] ([IdempotencyKey])
WHERE [IdempotencyKey] IS NOT NULL
```

Границы ответственности:

- Встроенная enqueue-idempotency работает только в `JobsHot`.
- Она предотвращает дублирование активной работы с одним business key.
- Когда исходное задание уходит в Archive или DLQ, тот же ключ можно поставить в очередь снова как новую логическую попытку.
- Долгоживущая память о том, что работа уже была завершена, относится к completion marker из optional idempotency middleware, а не к индексу Hot-очереди. Этот marker не replay'ит результат handler'а и не создает exactly-once side effects.

## Фаза 2: Fetch (Pending -> Fetched)

Fetcher loop в `SqlJobWorker` выполняется с polling-интервалом:

```csharp
// Simplified from SqlJobWorker.cs
while (!ct.IsCancellationRequested)
{
    var batch = await _storage.FetchNextBatchAsync(
        workerId: _workerId,
        batchSize: _options.BatchSize,
        allowedQueues: activeQueues
    );

    foreach (var job in batch)
    {
        // Push into bounded Channel<T> (Prefetch Buffer)
        await _channel.Writer.WriteAsync(job, ct);
    }

    await Task.Delay(_options.PollingInterval, ct);
}
```

**Fetch query атомарно:**
1. Выбирает top N pending-заданий с учетом priority, schedule и bulkhead-лимитов.
2. Устанавливает `Status = 1 (Fetched)` и назначает `WorkerId`.
3. Возвращает заблокированные строки.

## Фаза 3: Process (Fetched -> Processing)

Consumer loop читает задания из Prefetch Buffer:

```csharp
// For each job from the channel:
await _semaphore.WaitAsync(ct);      // DynamicConcurrencyLimiter — concurrency control
try
{
    await _processor.ProcessAsync(job, ct);
}
finally
{
    _semaphore.Release();
}
```

**Внутри `ProcessAsync()`:**

1. **Проверка Circuit Breaker** -> разрешен ли запуск этого job type.
2. **Mark as Processing** -> `Status = 2`, установка `HeartbeatUtc` и `StartedAtUtc`.
3. **Запуск heartbeat task** -> параллельно обновляет `HeartbeatUtc` каждые N секунд.
4. **Dispatch в handler** -> вызов через скомпилированный delegate на Expression Tree.
5. **Middleware pipeline** -> оборачивает handler onion-model стеком middleware.
6. **Result handling** -> путь Success, Fatal или Transient.

### Heartbeat Mechanism

Пока задание выполняется, параллельная task поддерживает его живым:

```csharp
// Simplified heartbeat logic
_ = Task.Run(async () =>
{
    while (!jobCt.IsCancellationRequested)
    {
        await Task.Delay(
            RandomBetween(
                options.Execution.HeartbeatIntervalMin,
                options.Execution.HeartbeatIntervalMax),
            jobCt);
        await _storage.KeepAliveAsync(jobId, jobCt);
    }
});
```

Это не дает `ZombieRescueService` заархивировать долгую, но здоровую работу.

## Фаза 4a: Success (Processing -> Archive)

```csharp
var durationMs = stopwatch.Elapsed.TotalMilliseconds;
await _storage.ArchiveSucceededAsync(job.Id, durationMs);
_breaker.ReportSuccess(job.Type);
_metrics.RecordSuccess(job.Queue, job.Type, durationMs);
```

Задание атомарно перемещается в `JobsArchive`, вместе с длительностью выполнения.

## Фаза 4b: Transient Failure (Processing -> Pending, retry)

```csharp
if (!IsFatalException(ex) && job.AttemptCount < options.Retry.MaxAttempts)
{
    var backoffMs = CalculateBackoffMs(job.AttemptCount + 1);
    var nextRun = DateTime.UtcNow.AddMilliseconds(backoffMs);

    await _storage.RescheduleForRetryAsync(
        job.Id, nextRun, job.AttemptCount + 1, ex.Message);
}
```

Задание **остается в `JobsHot`**, но:
- `Status` возвращается в `0 (Pending)`;
- `AttemptCount` увеличивается;
- `ScheduledAtUtc` устанавливается на будущее время с учетом backoff;
- задание не будет выбрано до наступления `ScheduledAtUtc`.

## Фаза 4c: Fatal Failure / Exhaustion (Processing -> DLQ)

```csharp
await _storage.ArchiveFailedAsync(job.Id, ex.ToString());
_breaker.ReportFailure(job.Type);
_metrics.RecordFailure(job.Queue, job.Type, ex.GetType().Name);
```

Задание атомарно перемещается в `JobsDLQ`, а полные сведения об exception сохраняются в `ErrorDetails`.

## Фаза 5: Resurrection (DLQ -> Hot)

Категории ошибок, которые приводят в DLQ, описаны в [Failure Taxonomy](/ru/2-lifecycle/failure-taxonomy).
Поведение shutdown и cancellation вокруг активной работы описано в [Graceful Shutdown](/ru/2-lifecycle/graceful-shutdown) и [Job Context And Cancellation](/ru/2-lifecycle/job-context-and-cancellation).

Через The Deck dashboard или программно:

```csharp
await _storage.ResurrectAsync(
    jobId: "job-123",
    updates: new JobDataUpdateDto
    {
        Payload = fixedJson,      // Optional: fix the broken payload
        Tags = "retried,manual",  // Optional: add audit tags
        Priority = 30             // Optional: boost priority
    },
    resurrectedBy: "admin@company.com"
);
```

Задание возвращается в `JobsHot` с:
- `Status = 0 (Pending)`;
- `AttemptCount = 0`, то есть fresh start;
- измененным payload/tags/priority, если они были переданы.

<br>

> *Дальше: как [Bulkhead Isolation](/ru/2-lifecycle/bulkhead-isolation) не дает тяжелым заданиям вытеснить легкие.*
