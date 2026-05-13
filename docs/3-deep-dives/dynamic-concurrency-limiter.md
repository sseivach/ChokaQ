# Dynamic Concurrency Limiter (Lock-Free)

## The Problem: Static Concurrency Limits

Standard `SemaphoreSlim` is initialized with a fixed capacity. Changing from 10 to 20 workers usually requires **restarting the application** or dealing with complex permit tracking and background cancellation tasks.

## The Solution: Logical Concurrency + Coalescing Channel

ChokaQ implements a completely lock-free, state-driven `DynamicConcurrencyLimiter` that drops the physical concept of "permits" entirely.

Instead of managing `SemaphoreSlim` slots, it uses two integers (`_activeWorkers`, `_targetCapacity`) and a highly optimized `Channel<byte>`.

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

> *Next: [In-Memory Engine](/3-deep-dives/memory-management) — BoundedChannels and backpressure.*
