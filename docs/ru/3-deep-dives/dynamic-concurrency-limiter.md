# Dynamic Concurrency Limiter (Lock-Free)

## Проблема: static concurrency limits

Обычный `SemaphoreSlim` инициализируется фиксированной capacity. Изменение с 10 до 20 workers обычно требует **restart application** или сложного permit tracking и background cancellation tasks.

## Решение: logical concurrency + coalescing channel

ChokaQ реализует полностью lock-free, state-driven `DynamicConcurrencyLimiter`, который вообще убирает физическую концепцию "permits".

Вместо управления `SemaphoreSlim` slots он использует два integers (`_activeWorkers`, `_targetCapacity`) и highly optimized `Channel<byte>`.

![Dynamic concurrency limiter](/diagrams/24-dynamic-concurrency-limiter.png)

### 1. Lock-free fast path

Когда worker пытается получить slot, он не ждет lock. Он использует atomic `CompareExchange` (CAS):

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

Это делает hot path, когда система под нагрузкой, но capacity есть, почти мгновенным: zero allocations и zero context switches.

### 2. Coalescing signal

Если capacity заполнена, worker уходит в slow path и ждет signal:

```csharp
private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
    new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

// ... inside Slow Path:
await _signal.Reader.ReadAsync(ct);
```

`Channel` с capacity `1` и `DropOldest` работает как asynchronous **edge-triggered wakeup**. Он гарантирует, что signals не накапливаются, предотвращая runaway CPU loops, но sleepers просыпаются при изменении state.

### 3. Relay wakeup (chain propagation)

Когда capacity увеличивается с 10 до 100, не нужно будить 90 sleeping threads одновременно: это проблема "Thundering Herd".

Вместо этого ChokaQ использует **Relay Wakeup** pattern. Когда worker успешно получает slot, он проверяет, осталась ли еще свободная capacity. Если да, он передает baton, будя ровно *one* more waiter:

```csharp
// Inside successful Fast Path:
int latestTarget = Volatile.Read(ref _targetCapacity);

if (current + 1 < latestTarget)
{
    _signal.Writer.TryWrite(1); // Wake exactly one more waiter
}
```

Так получается плавная chain-reaction scale-up, которая аккуратно увеличивает concurrency без удара по thread pool.

### 4. Zero-friction scaling

Так как implementation опирается только на проверку `_activeWorkers < _targetCapacity` внутри `while(true)`, динамическое изменение limit сводится к update variable и одному ping:

```csharp
public void SetCapacity(int newCapacity)
{
    Volatile.Write(ref _targetCapacity, newCapacity);

    // Kickstart the wakeup chain
    _signal.Writer.TryWrite(1);
}
```

Если вы scale down, например 20 -> 10, `_targetCapacity` обновляется сразу. Existing workers завершают текущие jobs и вызывают `Release()`, который decrement'ит `_activeWorkers`. Новые workers не пройдут Fast Path, пока `_activeWorkers` естественно не опустится ниже 10.

Без cancellation tokens, без background burner tasks, без permit leaks. Только state-driven mathematics.

### Misuse and shutdown contract

`Release()` должен соответствовать успешному `WaitAsync()`. Double release throws вместо silent repair counter, потому что hidden accounting bugs могут сделать worker capacity telemetry ложной.

Disposal будит waiting callers, а будущие `WaitAsync()` завершаются `ObjectDisposedException`. Release slot, acquired до disposal, разрешен во время shutdown, чтобы worker cleanup завершался нормально.

### Fairness

Limiter не обещает FIFO waiter fairness. Он использует competitive scheduling ради throughput и low overhead, что подходит для background job workers. Не используйте этот primitive как user-facing rate limiter без отдельного fairness review.

## Архитектурное решение

Limiter оптимизирован для background worker throughput, а не user-facing fairness. ChokaQ нужен primitive, который может менять target capacity во время runtime, когда The Deck меняет queue controls или host адаптирует worker pressure. Fixed `SemaphoreSlim` прост, но его сложно безопасно resize'ить без permit drift, leaked releases или restart worker manager.

Выбранный design держит authoritative state в counters: `_activeWorkers` показывает, сколько executions сейчас внутри critical section, а `_targetCapacity` - сколько разрешено. Scale down поэтому passive и safe: running jobs завершаются естественно, а новые jobs ждут, пока active count не станет ниже нового target.

Главный trade-off - fairness. Waiters конкурируют, когда capacity открывается, вместо strict FIFO queue. Для background jobs это приемлемо, потому что ordering work уже задается SQL priority, schedule time, queue bulkheads и retry policy.

## Дополнительные вопросы

**Почему не пересоздавать `SemaphoreSlim` при изменении capacity?**  
Потому что existing waiters и acquired permits принадлежат старому object. Swapping semaphores создает edge cases вокруг releases, cancellation и waiters, которые просыпаются на primitive, больше не являющемся authoritative.

**Как limiter избегает thundering-herd wakeups?**  
Через bounded one-item signal channel и relay wakeup. Capacity change emits one signal; каждый successful acquirer будит another waiter только если capacity еще остается.

**Какой invariant доказывает безопасность design?**  
Ни один caller не может войти, пока успешно не increment'ит `_activeWorkers` при observed active count ниже `_targetCapacity`. `Release()` decrement'ит тот же counter, а double release считается bug.

> *Дальше: [In-Memory Engine](/ru/3-deep-dives/memory-management) - BoundedChannels и backpressure.*
