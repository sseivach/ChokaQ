namespace ChokaQ.Core.Concurrency;

/// <summary>
/// A wrapper around SemaphoreSlim that allows dynamic scaling (Elasticity).
/// Supports hot-swapping concurrency limits without recreating the object.
/// </summary>
/// <remarks>
/// Used by JobWorker to dynamically adjust worker count via dashboard
/// without restarting the application.
/// 
/// Scale UP: Simply releases additional permits into the pool.
/// Scale DOWN: "Burns" permits by acquiring and never releasing them.
/// </remarks>
public class ElasticSemaphore : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly object _lock = new();
    private CancellationTokenSource? _burnCts;

    // The logical maximum concurrency we want to enforce.
    private int _targetCapacity;

    /// <summary>
    /// Gets the current maximum capacity (concurrency limit).
    /// </summary>
    public int Capacity => _targetCapacity;

    /// <summary>
    /// Gets the approximate number of threads currently occupying a slot.
    /// Formula: TargetCapacity - AvailablePermits
    /// </summary>
    public int RunningCount => _targetCapacity - _semaphore.CurrentCount;

    public ElasticSemaphore(int initialCapacity)
    {
        if (initialCapacity <= 0) initialCapacity = 1;

        _targetCapacity = initialCapacity;

        // Initialize with int.MaxValue to allow "infinite" scaling UP.
        // But we only release 'initialCapacity' permits to start.
        _semaphore = new SemaphoreSlim(initialCapacity, int.MaxValue);
    }

    /// <summary>
    /// Asynchronously waits to enter the semaphore.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        return _semaphore.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Exits the semaphore.
    /// </summary>
    public void Release()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Should not happen given int.MaxValue max capacity, 
            // but good to catch just in case logic drifts.
        }
    }

    /// <summary>
    /// Dynamically adjusts the semaphore capacity.
    /// Thread-safe and supports being called multiple times.
    /// </summary>
    /// <param name="newCapacity">The new target concurrency limit (minimum 1).</param>
    public void SetCapacity(int newCapacity)
    {
        if (newCapacity <= 0) newCapacity = 1;

        lock (_lock)
        {
            int diff = newCapacity - _targetCapacity;

            if (diff == 0) return;

            if (diff > 0)
            {
                // SCALE UP: Simply release more permits into the pool.
                _semaphore.Release(diff);
            }
            else
            {
                // SCALE DOWN: We need to remove permits. Since SemaphoreSlim 
                // doesn't have "Reduce", spawn a background task to consume ("burn") them.
                int permitsToBurn = Math.Abs(diff);

                // Cancel any previous burn operation
                _burnCts?.Cancel();
                _burnCts = new CancellationTokenSource();

                // Background burner task with cancellation support
                _ = BurnPermitsAsync(permitsToBurn, _burnCts.Token);
            }

            _targetCapacity = newCapacity;
        }
    }

    /// <summary>
    /// Consumes permits permanently to reduce capacity.
    /// Supports cancellation for graceful shutdown.
    /// </summary>
    private async Task BurnPermitsAsync(int count, CancellationToken ct)
    {
        for (int i = 0; i < count; i++)
        {
            try
            {
                // Acquire a permit...
                await _semaphore.WaitAsync(ct);

                // ...and NEVER release it. 
                // It is now lost to the void, effectively reducing capacity.
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested - stop burning permits
                break;
            }
        }
    }

    public void Dispose()
    {
        // Cancel any pending burn operations to allow graceful shutdown
        _burnCts?.Cancel();
        _burnCts?.Dispose();

        _semaphore.Dispose();
    }
}