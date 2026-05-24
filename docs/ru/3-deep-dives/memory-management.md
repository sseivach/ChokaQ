# In-Memory Engine

## Two Storage Modes, One Interface

Интерфейс `IJobStorage` в ChokaQ имеет две реализации:

| Implementation | Backing Store | Use Case |
|---------------|--------------|----------|
| `InMemoryJobStorage` | `ConcurrentDictionary` | Development, testing, high-throughput streaming |
| `SqlJobStorage` | SQL Server tables | Production, persistence, multi-instance |

Обе реализуют одну и ту же Three Pillars architecture - Hot, Archive, DLQ, только с разными backing stores.

![In-memory engine](/diagrams/57-in-memory-engine.png)

## InMemoryJobStorage: как это работает

Три `ConcurrentDictionary<string, T>` instance зеркалят SQL tables:

```csharp
private readonly ConcurrentDictionary<string, JobHotEntity> _hotJobs = new();
private readonly ConcurrentDictionary<string, JobArchiveEntity> _archiveJobs = new();
private readonly ConcurrentDictionary<string, JobDLQEntity> _dlqJobs = new();
```

### Capacity protection

```csharp
public InMemoryJobStorage(InMemoryStorageOptions options)
{
    _maxCapacity = options.MaxCapacity; // Default: 100,000
}

public ValueTask<string> EnqueueAsync(...)
{
    EnforceCapacity();
    _hotJobs[id] = job;
}
```

`MaxCapacity` - soft retention cap для in-process Three Pillars store. Когда cap достигнут, ChokaQ сначала evict'ит old Archive rows, затем old DLQ rows. Hot rows сохраняются, потому что это accepted work, которую worker еще должен обработать.

Без capacity policy runaway producer мог бы заполнить heap старой history. Для production workloads, где backlog должен переживать process restarts, используйте SQL Server mode и считайте `JobsHot` durable pressure boundary.

### Fetch: LINQ O(N) vs SQL index

In-memory fetch использует LINQ вместо SQL:

```csharp
public ValueTask<IEnumerable<JobHotEntity>> FetchNextBatchAsync(
    string workerId, int batchSize, string[]? allowedQueues, CancellationToken ct)
{
    var query = _hotJobs.Values
        .Where(j => j.Status == JobStatus.Pending)
        .Where(j => j.ScheduledAtUtc == null || j.ScheduledAtUtc <= DateTime.UtcNow);

    if (allowedQueues?.Length > 0)
        query = query.Where(j => allowedQueues.Contains(j.Queue));

    var batch = query
        .OrderByDescending(j => j.Priority)
        .ThenBy(j => j.ScheduledAtUtc ?? j.CreatedAtUtc)
        .Take(batchSize)
        .ToList();

    // Mark as Fetched (thread-safe via ConcurrentDictionary)
    foreach (var job in batch)
    {
        job.Status = JobStatus.Fetched;
        job.WorkerId = workerId;
    }

    return ValueTask.FromResult<IEnumerable<JobHotEntity>>(batch);
}
```

::: warning ⚠️ O(N) Complexity
LINQ scan'ит **все** jobs в dictionary. Для development с <10,000 jobs это нормально. Для production loads используйте `SqlJobStorage`, где filtered index дает O(log N) lookups.
:::

## Prefetch buffer: BoundedChannel

Между fetcher и processor находится `Channel<JobHotEntity>`:

```csharp
// SqlJobWorker.cs
private readonly Channel<JobHotEntity> _channel =
    Channel.CreateBounded<JobHotEntity>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = true
    });
```

### Почему bounded channel?

| Property | Value | Purpose |
|----------|-------|---------|
| `Capacity` | 100 | Maximum jobs buffered in memory |
| `FullMode` | `Wait` | Fetcher blocks when buffer is full - **backpressure** |
| `SingleWriter` | `true` | Only the fetcher loop writes - allows internal optimization |
| `SingleReader` | `false` | Multiple processor tasks read concurrently |

### Producer-consumer flow

![In-memory bounded channel flow](/diagrams/57-in-memory-engine.png)

**Почему этот design предотвращает memory issues:**

1. **Fetcher** берет batch, например 50 jobs, из SQL.
2. Push'ит их в channel one by one.
3. Если channel full, 100 jobs buffered, `WriteAsync` **blocks**.
4. Fetcher перестает poll'ить SQL, пока processors не drain'ят buffer.
5. Нет unbounded memory growth - максимум 100 jobs в RAM в любой момент.

### Backpressure in action

```
Scenario: Processing is slow (10 jobs/sec), but fetcher gets 50 jobs/batch

t=0s:   Channel: [                    ] (0/100) — Fetcher gets 50, pushes all
t=0s:   Channel: [████████████████████] (50/100) — Processing starts
t=5s:   Channel: [████████████████    ] (40/100) — 10 processed, fetcher gets 50 more
t=5s:   Channel: [████████████████████████████] (90/100)
t=10s:  Channel: [██████████████████████████  ] (80/100) — 10 more processed
t=10s:  Fetcher gets 50, pushes 20 then BLOCKS ← backpressure kicks in
t=15s:  10 more processed, fetcher unblocks, pushes remaining 30
```

System self-regulates. SQL polling rate автоматически адаптируется к processing capacity.

## InMemoryQueue: bounded producer channel

Для in-memory mode `InMemoryQueue` использует bounded channel job objects. Этот channel одновременно worker notification mechanism и producer-side backpressure boundary:

```csharp
public class InMemoryQueue : IChokaQQueue
{
    private readonly Channel<IChokaQJob> _queue =
        Channel.CreateBounded<IChokaQJob>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    public async Task EnqueueAsync<TJob>(TJob job, ...)
    {
        var id = await _storage.EnqueueAsync(...);
        await _queue.Writer.WriteAsync(job, ct);
    }
}
```

`JobWorker` читает из этого channel вместо polling:

```csharp
// No polling needed; the channel is the notification mechanism.
while (await _queue.Reader.WaitToReadAsync(ct))
{
    while (_queue.Reader.TryRead(out var job))
    {
        var storageJob = await _storage.GetJobAsync(job.Id, ct);
        if (storageJob != null)
            await _processor.ProcessJobAsync(...);
    }
}
```

Это event-driven: zero CPU usage when idle, instant processing при arrival job и bounded producer memory, когда burst превышает worker throughput.

## Memory safety summary

| Protection | Mechanism | Limit |
|-----------|-----------|-------|
| In-process history | `InMemory.MaxCapacity` evicts old Archive/DLQ rows | 100,000 default |
| Producer channel | `BoundedChannel<IChokaQJob>` with `Wait` mode | 100,000 jobs |
| Prefetch buffer | `BoundedChannel` with `Wait` mode | 100 jobs |
| Processing concurrency | `DynamicConcurrencyLimiter` | Configurable |
| Archive/DLQ growth | Evicted before Hot rows when capacity is reached | Soft cap |

::: tip Architecture Insight
In-memory mode защищает process через layered pressure controls: `InMemory.MaxCapacity` ограничивает retained history, bounded producer channel замедляет callers, когда workers отстают, а `DynamicConcurrencyLimiter` ограничивает active execution. SQL Server mode выносит backlog из process и использует database как durable pressure boundary.
:::

## Архитектурное решение

In-memory engine существует, чтобы local development, tests и volatile process-owned workloads были быстрыми без изменения public queue API. Он достаточно близко зеркалит SQL model, чтобы handlers, middleware, dispatch, retries и dashboard concepts оставались понятными в обоих modes.

Он не позиционируется как production durability story. `ConcurrentDictionary` и bounded channels - отличные in-process tools, но они не могут coordinate multiple hosts, пережить process crash или дать database-level audit history. Поэтому documentation рассматривает SQL Server mode как production default, а in-memory mode - как осознанный local или ephemeral choice.

Важная engineering boundary: in-memory mode все равно имеет pressure limits. Он не использует "demo mode" как оправдание unbounded heap growth.

## Дополнительные вопросы

**Зачем вообще in-memory provider, если production path - SQL Server?**  
Потому что storage abstraction легче доверять, когда она может запускать быстрые tests и samples без infrastructure. In-memory provider сокращает feedback loops, сохраняя тот же handler-facing contract.

**Что ломается, если использовать in-memory mode для critical production work?**  
Accepted jobs исчезают при process loss, queue limits process-local, а history нельзя share'ить across instances. Это нарушает durability и coordination guarantees, ожидаемые от production background processing.

**Почему preserve Hot rows при capacity eviction?**  
Hot rows представляют accepted unfinished work. Evict Archive или DLQ history менее вредно, чем discard work, которую еще нужно выполнить.

> Дальше: [Backpressure Policy](/ru/3-deep-dives/backpressure-policy) или [The Deck Dashboard](/ru/4-the-deck/realtime-signalr).
