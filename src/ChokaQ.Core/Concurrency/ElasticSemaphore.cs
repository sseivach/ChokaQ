using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ChokaQ.Core.Concurrency;

/// <summary>
/// Production-grade elastic concurrency limiter.
///
/// KEY DESIGN PRINCIPLES:
///
/// 1. Logical Concurrency Control (No Physical Permits)
///    ------------------------------------------------
///    We do NOT rely on SemaphoreSlim permits.
///    Instead, concurrency is enforced via:
///      - _activeWorkers (current running count)
///      - _targetCapacity (limit)
///
///    This eliminates:
///      - permit leaks
///      - race conditions during resizing
///      - complex synchronization bugs
///
/// 2. Coalescing Signal (Bounded Channel)
///    ----------------------------------
///    Channel with capacity = 1 and DropOldest ensures:
///      - signals NEVER accumulate
///      - prevents runaway CPU loops
///      - acts as an async "edge-triggered" wakeup
///
/// 3. Relay Wakeup (Chain Propagation)
///    --------------------------------
///    Each successful worker wakes exactly ONE next waiter,
///    if capacity still allows.
///
///    This ensures:
///      - fast scale-up (no "stuck sleepers")
///      - no thundering herd
///      - smooth capacity ramp-up
///
/// 4. State-Driven Model (Critical)
///    -----------------------------
///    Signals DO NOT guarantee availability.
///    They only mean:
///      "state may have changed — re-check"
///
///    Correctness is achieved by:
///      - loop + re-check pattern
///
/// 5. Lock-Free Fast Path
///    -------------------
///    Uses Interlocked.CompareExchange for minimal overhead.
///
/// 6. Contention Backoff
///    ------------------
///    SpinWait + Task.Yield prevents CPU burn under heavy contention.
///
///
/// LIMITATIONS (Explicit by design):
/// --------------------------------
/// - No strict FIFO fairness (competitive scheduling)
/// - Possible starvation in extreme edge cases
/// - Designed for throughput, not strict ordering
///
///
/// This implementation is suitable for:
/// - high-throughput job processors
/// - background workers
/// - distributed task runners
/// </summary>
public class ElasticSemaphore : IDisposable
{
    private int _targetCapacity;
    private int _activeWorkers;

    /// <summary>
    /// Coalescing async signal (binary event).
    ///
    /// IMPORTANT:
    /// - Capacity = 1 → acts like AutoResetEvent
    /// - DropOldest → prevents signal accumulation
    /// </summary>
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private volatile bool _disposed;

    /// <summary>
    /// Current concurrency limit.
    /// </summary>
    public int Capacity => Volatile.Read(ref _targetCapacity);

    /// <summary>
    /// Current number of running workers.
    /// </summary>
    public int RunningCount => Volatile.Read(ref _activeWorkers);

    public ElasticSemaphore(int initialCapacity)
    {
        if (initialCapacity < 1) initialCapacity = 1;
        _targetCapacity = initialCapacity;
    }

    /// <summary>
    /// Asynchronously acquires a logical execution slot.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        SpinWait spin = new();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_disposed)
                throw new ObjectDisposedException(nameof(ElasticSemaphore));

            int current = Volatile.Read(ref _activeWorkers);
            int target = Volatile.Read(ref _targetCapacity);

            // =========================
            // FAST PATH (Lock-Free)
            // =========================
            if (current < target)
            {
                if (Interlocked.CompareExchange(ref _activeWorkers, current + 1, current) == current)
                {
                    // RELAY WAKEUP:
                    // If capacity still available, wake exactly ONE more waiter.
                    int latestTarget = Volatile.Read(ref _targetCapacity);

                    if (current + 1 < latestTarget)
                    {
                        _signal.Writer.TryWrite(1);
                    }

                    return;
                }

                // CAS failed → contention
                // Use adaptive backoff to avoid CPU burn
                if (spin.Count > 10)
                {
                    await Task.Yield();
                }
                else
                {
                    spin.SpinOnce();
                }

                continue;
            }

            // =========================
            // SLOW PATH (Async Wait)
            // =========================
            try
            {
                await _signal.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                throw new ObjectDisposedException(nameof(ElasticSemaphore));
            }

            // After wakeup → ALWAYS re-check state
        }
    }

    /// <summary>
    /// Releases a logical execution slot.
    /// </summary>
    public void Release()
    {
        int newValue = Interlocked.Decrement(ref _activeWorkers);

        // Defensive protection against misuse (double release)
        if (newValue < 0)
        {
            Interlocked.Exchange(ref _activeWorkers, 0);
            newValue = 0;
        }

        // Wake one waiter if capacity allows
        if (newValue < Volatile.Read(ref _targetCapacity))
        {
            _signal.Writer.TryWrite(1);
        }
    }

    /// <summary>
    /// Dynamically updates concurrency limit.
    /// </summary>
    public void SetCapacity(int newCapacity)
    {
        if (newCapacity < 1) newCapacity = 1;

        Volatile.Write(ref _targetCapacity, newCapacity);

        // Kickstart wakeup chain (relay will propagate further)
        _signal.Writer.TryWrite(1);
    }

    public void Dispose()
    {
        _disposed = true;

        // Wake at least one waiter to speed up shutdown
        _signal.Writer.TryWrite(1);

        _signal.Writer.TryComplete();
    }
}