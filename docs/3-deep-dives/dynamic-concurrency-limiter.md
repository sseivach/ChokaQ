# Dynamic Concurrency Limiter (Lock-Free)

## The Problem: Static Concurrency Limits

Standard `SemaphoreSlim` is initialized with a fixed capacity. Changing from 10 to 20 workers usually requires **restarting the application** or dealing with complex permit tracking and background cancellation tasks.

## The Solution: Logical Concurrency + Coalescing Channel

ChokaQ implements a completely lock-free, state-driven `DynamicConcurrencyLimiter` that drops the physical concept of "permits" entirely.

Instead of managing `SemaphoreSlim` slots, it uses two integers (`_activeWorkers`, `_targetCapacity`) and a highly optimized `Channel<byte>`.

![Dynamic concurrency limiter](/diagrams/24-dynamic-concurrency-limiter.png)

### 1. Lock-Free Fast Path

When a worker tries to acquire a slot, it doesn't wait on a lock. It uses an atomic `CompareExchange` (CAS) operation:

```csharp
int current = Volatile.Read(ref _activeWorkers);
int target = Volatile.Read(ref _targetCapacity);

if (current < target)
{
    // Try to atomically claim the slot
    if (Interlocked.CompareExchange(ref _activeWorkers, current + 1, current) == current)
    {
        // Slot acquired!
        return; 
    }
}
```

This makes the "hot path" (when the system is under load but has capacity) nearly instantaneous, with zero allocations and zero context switches.

### 2. The Coalescing Signal

If the capacity is full, the worker drops into the "Slow Path" and awaits a signal:

```csharp
private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
    new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

// ... inside Slow Path:
await _signal.Reader.ReadAsync(ct);
```

By configuring the `Channel` with a capacity of `1` and `DropOldest`, it acts as an asynchronous **edge-triggered wakeup**. It guarantees that signals never accumulate, preventing runaway CPU loops, while ensuring that sleepers are woken up when state changes.

### 3. Relay Wakeup (Chain Propagation)

When capacity is increased from 10 to 100, we don't want to wake 90 sleeping threads simultaneously (the "Thundering Herd" problem). 

Instead, ChokaQ uses a **Relay Wakeup** pattern. When a worker successfully acquires a slot, it checks if there is *still* more capacity available. If yes, it passes the baton by waking exactly *one* more waiter:

```csharp
// Inside successful Fast Path:
int latestTarget = Volatile.Read(ref _targetCapacity);

if (current + 1 < latestTarget)
{
    _signal.Writer.TryWrite(1); // Wake exactly one more waiter
}
```

This creates a smooth, chain-reaction scale-up that gracefully ramps up concurrency without slamming the thread pool.

### 4. Zero-Friction Scaling

Because the implementation relies purely on evaluating `_activeWorkers < _targetCapacity` inside a `while(true)` loop, dynamically changing the limit is as simple as updating a variable and sending a single ping:

```csharp
public void SetCapacity(int newCapacity)
{
    Volatile.Write(ref _targetCapacity, newCapacity);

    // Kickstart the wakeup chain
    _signal.Writer.TryWrite(1);
}
```

If you scale down (e.g., 20 → 10), `_targetCapacity` is instantly updated. Existing workers finish their current jobs and call `Release()`, which decrements `_activeWorkers`. No new workers will be allowed to pass the Fast Path until `_activeWorkers` naturally drains below 10. 

No cancellation tokens, no background burner tasks, no permit leaks. Just pure, state-driven mathematics.

### Misuse And Shutdown Contract

`Release()` must match a successful `WaitAsync()`. A double release throws
instead of silently repairing the counter, because hidden accounting bugs can
make worker capacity telemetry lie.

Disposal wakes waiting callers and future `WaitAsync()` calls fail with
`ObjectDisposedException`. Releasing a slot that was acquired before disposal is
allowed during shutdown, so worker cleanup can finish normally.

### Fairness

The limiter does not promise FIFO waiter fairness. It uses competitive
scheduling for throughput and low overhead, which is appropriate for background
job workers. Do not use this primitive as a user-facing rate limiter without a
separate fairness review.

## Architecture Decision

The limiter is optimized for background worker throughput, not user-facing
fairness. ChokaQ needs a primitive that can change target capacity at runtime
when The Deck changes queue controls or when the host adapts worker pressure.
A fixed `SemaphoreSlim` is simple, but it is awkward to resize safely without
permit drift, leaked releases, or restarting the worker manager.

The chosen design keeps the authoritative state in counters:
`_activeWorkers` tells how many executions are currently inside the critical
section, and `_targetCapacity` tells how many are allowed. Scaling down is
therefore passive and safe: running jobs finish naturally, and new jobs wait
until active count drops below the new target.

The main trade-off is fairness. Waiters compete when capacity opens instead of
being served by a strict FIFO queue. For background jobs this is acceptable
because work ordering is already governed by SQL priority, schedule time, queue
bulkheads, and retry policy.

## Additional Questions

**Why not just recreate a `SemaphoreSlim` when capacity changes?**  
Because existing waiters and acquired permits belong to the old object. Swapping
semaphores creates edge cases around releases, cancellation, and waiters waking
on a primitive that is no longer authoritative.

**How does the limiter avoid thundering-herd wakeups?**  
It uses a bounded one-item signal channel and relay wakeup. A capacity change
emits one signal; each successful acquirer wakes another waiter only if more
capacity remains.

**What invariant proves the design is safe?**  
No caller can enter unless it successfully increments `_activeWorkers` while the
observed active count is below `_targetCapacity`. `Release()` decrements the same
counter and double release is treated as a bug.
 
> *Next: [In-Memory Engine](/3-deep-dives/memory-management) — BoundedChannels and backpressure.*
