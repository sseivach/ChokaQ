namespace ChokaQ.Core.Concurrency;

/// <summary>
/// A wrapper around SemaphoreSlim that allows dynamic scaling (Elasticity).
/// Supports hot-swapping concurrency limits without recreating the object.
/// </summary>
public class ElasticSemaphore : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly object _lock = new();

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
    /// </summary>
    /// <param name="newCapacity">The new target concurrency limit.</param>
    public void SetCapacity(int newCapacity)
    {
        if (newCapacity <= 0) newCapacity = 1;

        lock (_lock)
        {
            int diff = newCapacity - _targetCapacity;

            if (diff == 0) return;

            if (diff > 0)
            {
                // SCALE UP:
                // Simply release more permits into the pool.
                _semaphore.Release(diff);
            }
            else
            {
                // SCALE DOWN:
                // We need to remove permits. Since SemaphoreSlim doesn't have "Reduce",
                // we spawn a background task to consume ("burn") them.
                int permitsToBurn = Math.Abs(diff);

                // Fire-and-forget burner task
                Task.Run(async () => await BurnPermitsAsync(permitsToBurn));
            }

            _targetCapacity = newCapacity;
        }
    }

    /// <summary>
    /// Consumes permits permanently to reduce capacity.
    /// </summary>
    private async Task BurnPermitsAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            // Acquire a permit...
            await _semaphore.WaitAsync();

            // ...and NEVER release it. 
            // It is now lost to the void, effectively reducing capacity.
        }
    }

    public void Dispose()
    {
        // We only hold managed resources (SemaphoreSlim), so we simply dispose of them.
        // No Finalizer (~ElasticSemaphore) is needed, hence no GC.SuppressFinalize(this).
        _semaphore.Dispose();
    }
}