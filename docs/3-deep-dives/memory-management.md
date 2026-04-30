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
    if (_hotJobs.Count >= _maxCapacity)
        throw new InvalidOperationException(
            $"In-memory storage capacity exceeded ({_maxCapacity})");
    // ...
}
```

Without a capacity limit, a runaway producer could fill the heap and crash the process with `OutOfMemoryException`.

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

```
┌──────────────┐     ┌─────────────────────────┐     ┌──────────────┐
│   FETCHER    │     │    BOUNDED CHANNEL       │     │  PROCESSORS  │
│  (Producer)  │────▶│  [job][job][job]...[job]  │────▶│  (Consumers) │
│              │     │     capacity: 100        │     │              │
│  Polls SQL   │     │                          │     │  Parallel    │
│  every 5s    │     │  Backpressure: Writer    │     │  via Elastic │
│              │     │  blocks when full        │     │  Semaphore   │
└──────────────┘     └─────────────────────────┘     └──────────────┘
```

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

## InMemoryQueue: Channel-Based Notifications

For the In-Memory mode, `InMemoryQueue` uses an unbounded channel to notify the worker about new jobs:

```csharp
public class InMemoryQueue : IChokaQQueue
{
    private readonly Channel<string> _signal = Channel.CreateUnbounded<string>();

    public async ValueTask<string> EnqueueAsync(IChokaQJob job, ...)
    {
        var id = await _storage.EnqueueAsync(...);
        // Signal the worker that a new job is available
        await _signal.Writer.WriteAsync(id);
        return id;
    }
}
```

The `JobWorker` reads from this signal channel instead of polling:

```csharp
// No polling needed — the channel IS the notification mechanism
await foreach (var jobId in _signal.Reader.ReadAllAsync(ct))
{
    var job = await _storage.GetJobAsync(jobId, ct);
    if (job != null)
        await _processor.ProcessAsync(job, ct);
}
```

This is **event-driven** — zero CPU usage when idle, instant processing when a job arrives.

## Memory Safety Summary

| Protection | Mechanism | Limit |
|-----------|-----------|-------|
| Hot table size | `MaxCapacity` check in `EnqueueAsync` | 100,000 default |
| Prefetch buffer | `BoundedChannel` with `Wait` mode | 100 jobs |
| Processing concurrency | `ElasticSemaphore` | Configurable |
| Archive/DLQ growth | In-memory: unlimited (dev only) | N/A |

::: tip 💡 Architecture Insight
OutOfMemory exceptions are prevented through three layers: `MaxCapacity` caps total jobs, `BoundedChannel` caps the fetch buffer with backpressure, and `ElasticSemaphore` caps concurrent processing. Each layer independently prevents resource exhaustion.
:::

> *Next: Explore [The Deck Dashboard](/4-the-deck/realtime-signalr) — real-time monitoring with SignalR.*
