# CHK-02: Job State Machine

## The Path of a Job

Every job in ChokaQ follows a deterministic path through a series of states. Understanding this path is the key to understanding the entire system.

## The States

| Status | Value | Location | Meaning |
|--------|-------|----------|---------|
| **Pending** | `0` | JobsHot | Waiting in queue — ready to be picked up |
| **Fetched** | `1` | JobsHot | Pulled into worker memory buffer — waiting for processing slot |
| **Processing** | `2` | JobsHot | Actively executing — heartbeat ticking |
| **Succeeded** | `3` | JobsArchive | Completed successfully — moved to archive |
| **Failed** | `4` | JobsDLQ | Failed permanently — moved to morgue |
| **Cancelled** | `5` | JobsDLQ | Admin cancelled — moved to morgue |
| **Zombie** | `6` | JobsDLQ | Worker crashed — heartbeat expired |

::: info 📝 Note
Statuses 3–6 are **virtual** in SQL Server mode. The job doesn't actually have a `Status` column set to 3 — it's simply moved to the `JobsArchive` table. These enum values exist for SignalR notifications and the In-Memory storage mode.
:::

## The Full Lifecycle

<img src="/state_machine.png" alt="Job State Machine" style="width: 100%; max-width: 900px; margin: 1.5rem auto; display: block;" />

## Phase 1: Enqueue

When `IChokaQQueue.EnqueueAsync()` is called:

```csharp
await _queue.EnqueueAsync(
    new SendEmailJob("user@example.com", "Welcome!"),
    priority: 20,              // Higher = processed first
    delay: TimeSpan.FromMinutes(5),  // Optional delayed execution
    idempotencyKey: "email-user-123" // Optional duplicate prevention
);
```

**What happens internally:**

1. Generate unique ID (`ULID` format for time-sortable IDs)
2. Serialize job DTO to JSON
3. **Idempotency check:** If key exists → return existing job ID, skip insert
4. Insert into `JobsHot` with `Status = 0 (Pending)`
5. Set `ScheduledAtUtc` if delay is specified
6. Record `chokaq.jobs.enqueued` metric
7. Push notification via SignalR

### Idempotency Guard

```sql
-- Check BEFORE insert to avoid unique constraint violations
SELECT [Id] FROM [chokaq].[JobsHot]
WHERE [IdempotencyKey] = @Key;

-- If found → return existing ID, done
-- If not → proceed with INSERT
```

Plus a unique filtered index as a safety net:

```sql
CREATE UNIQUE NONCLUSTERED INDEX [IX_JobsHot_Idempotency]
ON [chokaq].[JobsHot] ([IdempotencyKey])
WHERE [IdempotencyKey] IS NOT NULL
```

## Phase 2: Fetch (Pending → Fetched)

The `SqlJobWorker`'s fetcher loop runs on a polling interval:

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

**The fetch query atomically:**
1. Selects top N pending jobs (respecting priority, schedule, and bulkhead limits)
2. Sets `Status = 1 (Fetched)` and assigns `WorkerId`
3. Returns the locked rows

## Phase 3: Process (Fetched → Processing)

The consumer loop reads from the Prefetch Buffer:

```csharp
// For each job from the channel:
await _semaphore.WaitAsync(ct);      // ElasticSemaphore — concurrency control
try
{
    await _processor.ProcessAsync(job, ct);
}
finally
{
    _semaphore.Release();
}
```

**Inside `ProcessAsync()`:**

1. **Circuit Breaker check** → is this job type allowed to execute?
2. **Mark as Processing** → `Status = 2`, set `HeartbeatUtc` and `StartedAtUtc`
3. **Start heartbeat task** → updates `HeartbeatUtc` every N seconds (parallel)
4. **Dispatch to handler** → Expression Tree compiled delegate invocation
5. **Middleware pipeline** → wraps handler in onion-model middleware stack
6. **Result handling** → Success, Fatal, or Transient path

### Heartbeat Mechanism

While the job is processing, a parallel task keeps it alive:

```csharp
// Simplified heartbeat logic
_ = Task.Run(async () =>
{
    while (!jobCt.IsCancellationRequested)
    {
        await Task.Delay(HeartbeatInterval, jobCt);
        await _storage.KeepAliveAsync(jobId, jobCt);
    }
});
```

This prevents the `ZombieRescueService` from archiving a long-running but healthy job.

## Phase 4a: Success (Processing → Archive)

```csharp
var durationMs = stopwatch.Elapsed.TotalMilliseconds;
await _storage.ArchiveSucceededAsync(job.Id, durationMs);
_breaker.ReportSuccess(job.Type);
_metrics.RecordSuccess(job.Queue, job.Type, durationMs);
```

The job is atomically moved to `JobsArchive` with execution duration recorded.

## Phase 4b: Transient Failure (Processing → Pending, retry)

```csharp
if (!IsFatalException(ex) && job.AttemptCount < MaxRetries)
{
    var backoffMs = CalculateBackoffMs(job.AttemptCount + 1);
    var nextRun = DateTime.UtcNow.AddMilliseconds(backoffMs);

    await _storage.RescheduleForRetryAsync(
        job.Id, nextRun, job.AttemptCount + 1, ex.Message);
}
```

The job **stays in `JobsHot`** but:
- `Status` → back to `0 (Pending)`
- `AttemptCount` → incremented
- `ScheduledAtUtc` → set to future time (backoff)
- Won't be fetched until `ScheduledAtUtc` passes

## Phase 4c: Fatal Failure / Exhaustion (Processing → DLQ)

```csharp
await _storage.ArchiveFailedAsync(job.Id, ex.ToString());
_breaker.ReportFailure(job.Type);
_metrics.RecordFailure(job.Queue, job.Type, ex.GetType().Name);
```

The job is atomically moved to `JobsDLQ` with full exception details stored in `ErrorDetails`.

## Phase 5: Resurrection (DLQ → Hot)

Via The Deck dashboard or programmatically:

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

The job returns to `JobsHot` with:
- `Status = 0 (Pending)`
- `AttemptCount = 0` (fresh start)
- Modified payload/tags/priority (if provided)

<br>

> *Next: Learn how [Bulkhead Isolation](/2-lifecycle/bulkhead-isolation) prevents heavy jobs from starving lightweight ones.*
