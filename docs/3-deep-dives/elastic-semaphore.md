# Elastic Semaphore

## The Problem: Static Concurrency Limits

Standard `SemaphoreSlim` is initialized with a fixed capacity:

```csharp
var semaphore = new SemaphoreSlim(initialCount: 10, maxCount: 10);
```

Changing from 10 to 20 workers requires **restarting the application**.

## The Solution: ElasticSemaphore

ChokaQ wraps `SemaphoreSlim` with dynamic scaling:

```csharp
// From: ChokaQ.Core/Concurrency/ElasticSemaphore.cs

public class ElasticSemaphore
{
    private readonly SemaphoreSlim _semaphore;
    private int _targetCapacity;
    private int _actualCapacity;
    private CancellationTokenSource? _burnCts;

    public ElasticSemaphore(int initialCapacity)
    {
        _currentCapacity = initialCapacity;
        // MaxCount = int.MaxValue allows infinite upward scaling
        _semaphore = new SemaphoreSlim(initialCapacity, int.MaxValue);
    }
}
```

The trick: `maxCount = int.MaxValue` allows `Release()` beyond the initial count.

## Scaling Up: Minting Permits

```csharp
if (diff > 0)
{
    _semaphore.Release(diff);  // "Mint" new permits
    _actualCapacity += diff;
}
```

Capacity 10 → 15: call `_semaphore.Release(5)`. Workers immediately see 5 more slots.

## Scaling Down: The Burn Pattern

`SemaphoreSlim` has no `Reduce()`. So we **acquire** permits and **never release them**:

```csharp
private async Task BurnPermitsAsync(int count, CancellationToken ct)
{
    for (int i = 0; i < count; i++)
    {
        await _semaphore.WaitAsync(ct);
        // ...and NEVER release it. The permit is destroyed.
        Interlocked.Decrement(ref _actualCapacity);
    }
}
```

Capacity 15 → 10: burn tasks acquire 5 permits in the background. If all slots are busy, burns **wait gracefully** — no workers are interrupted.

::: warning 🎯 Key Insight
Burning is **asynchronous and non-blocking**. Capacity gradually decreases as jobs complete. This is a graceful drain, not a hard cut.
:::

## Defensive Cancellation (Anti-Drift)

Rapid resizes (15→10→20) dynamically cancel old burn tasks to prevent "permit drift". The engine tracks the *physical* `_actualCapacity` rather than just the logical target:

```csharp
// Cancel ANY previous burn operation (Up or Down)
_burnCts?.Cancel(); 

// Calculate based on physical reality, not target
int diff = _targetCapacity - _actualCapacity; 
```

This defensive approach ensures that if a previous scale-down operation was interrupted, the math self-corrects perfectly on the next adjustment.

## Integration with SqlJobWorker

```csharp
await _semaphore.WaitAsync(ct);  // Wait for processing slot
try {
    await _processor.ProcessAsync(job, ct);
} finally {
    _semaphore.Release();         // Return the permit
}
```

Admin changes concurrency via The Deck → `_semaphore.Resize(newLimit)` → instant effect.

::: tip 💡 Design Decision
Recreating a `SemaphoreSlim` with the new capacity would require waiting for all existing `WaitAsync()` calls to complete before disposing the old instance. During that drain period, no new work could start. The burn pattern solves this, allowing **seamless** capacity changes with zero downtime — existing workers keep running, only the available slot count changes.
:::

> *Next: [In-Memory Engine](/3-deep-dives/memory-management) — BoundedChannels and backpressure.*
