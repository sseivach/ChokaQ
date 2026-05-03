# In-Memory Engine

## Two Storage Modes, One Interface

ChokaQ's `IJobStorage` interface has two implementations:

| Implementation | Backing Store | Use Case |
|---------------|--------------|----------|
| `InMemoryJobStorage` | `ConcurrentDictionary` | Development, testing, high-throughput streaming |
| `SqlJobStorage` | SQL Server tables | Production, persistence, multi-instance |

Both implement the same Three Pillars architecture — Hot, Archive, DLQ — just with different backing stores.

## InMemoryJobStorage: How It Works

Three `ConcurrentDictionary<string, T>` instances mirror the SQL tables:

```csharp
private readonly ConcurrentDictionary<string, JobHotEntity> _hotJobs = new();
private readonly ConcurrentDictionary<string, JobArchiveEntity> _archiveJobs = new();
private readonly ConcurrentDictionary<string, JobDLQEntity> _dlqJobs = new();
```

### Capacity Protection

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

`MaxCapacity` is a soft retention cap for the in-process Three Pillars store.
When the cap is reached, ChokaQ evicts old Archive rows first and old DLQ rows
second. Hot rows are preserved because they are accepted work that the worker
still needs to process.

Without a capacity policy, a runaway producer could fill the heap with old
history. For production workloads where the backlog must survive process
restarts, use SQL Server mode and let `JobsHot` be the durable pressure boundary.

### Fetch: LINQ O(N) vs SQL Index

In-memory fetch uses LINQ instead of SQL:

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
LINQ scans **all** jobs in the dictionary. For development with <10,000 jobs this is fine. For production loads, use `SqlJobStorage` where the filtered index provides O(log N) lookups.
:::

## The Prefetch Buffer: BoundedChannel

Between the fetcher and processor sits a `Channel<JobHotEntity>`:

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

### Why a Bounded Channel?

| Property | Value | Purpose |
|----------|-------|---------|
| `Capacity` | 100 | Maximum jobs buffered in memory |
| `FullMode` | `Wait` | Fetcher blocks when buffer is full — **backpressure** |
| `SingleWriter` | `true` | Only the fetcher loop writes — allows internal optimization |
| `SingleReader` | `false` | Multiple processor tasks read concurrently |

### The Producer-Consumer Flow

<img src="/bounded_channel.png" alt="Bounded Channel Flow Diagram" style="width: 100%; max-width: 900px; margin: 1.5rem auto; display: block;" />

**Why this design prevents memory issues:**

1. **Fetcher** grabs a batch (e.g., 50 jobs) from SQL
2. Pushes them into the channel one by one
3. If channel is full (100 jobs buffered), `WriteAsync` **blocks**
4. Fetcher stops polling SQL until processors drain the buffer
5. No unbounded memory growth — maximum 100 jobs in RAM at any time

### Backpressure in Action

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

The system self-regulates. The SQL polling rate automatically adapts to processing capacity.

## InMemoryQueue: Bounded Producer Channel

For the in-memory mode, `InMemoryQueue` uses a bounded channel of job objects.
This channel is both the worker notification mechanism and the producer-side
backpressure boundary:

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

The `JobWorker` reads from this channel instead of polling:

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

This is event-driven: zero CPU usage when idle, instant processing when a job
arrives, and bounded producer memory when a burst exceeds worker throughput.

## Memory Safety Summary

| Protection | Mechanism | Limit |
|-----------|-----------|-------|
| In-process history | `InMemory.MaxCapacity` evicts old Archive/DLQ rows | 100,000 default |
| Producer channel | `BoundedChannel<IChokaQJob>` with `Wait` mode | 100,000 jobs |
| Prefetch buffer | `BoundedChannel` with `Wait` mode | 100 jobs |
| Processing concurrency | `DynamicConcurrencyLimiter` | Configurable |
| Archive/DLQ growth | Evicted before Hot rows when capacity is reached | Soft cap |

::: tip Architecture Insight
In-memory mode protects the process through layered pressure controls:
`InMemory.MaxCapacity` limits retained history, the bounded producer channel slows
callers when workers fall behind, and `DynamicConcurrencyLimiter` caps active
execution. SQL Server mode moves the backlog out of process and uses the
database as the durable pressure boundary.
:::

> Next: Read the [Backpressure Policy](/3-deep-dives/backpressure-policy) or explore [The Deck Dashboard](/4-the-deck/realtime-signalr).
