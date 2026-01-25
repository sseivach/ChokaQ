using ChokaQ.Abstractions.Resilience;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// A zero-dependency, in-memory implementation of the deduplicator.
/// Uses a ConcurrentDictionary to track active keys and a background timer to cleanup expired entries.
/// </summary>
public class InMemoryDeduplicator : IDeduplicator, IDisposable
{
    private readonly ConcurrentDictionary<string, DateTime> _locks = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(1);
    private bool _disposed;

    public InMemoryDeduplicator()
    {
        // Run cleanup every minute
        _cleanupTimer = new Timer(CleanupCycle, null, _cleanupInterval, _cleanupInterval);
    }

    public ValueTask<bool> TryAcquireAsync(string key, TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(key)) return new ValueTask<bool>(true);

        var now = DateTime.UtcNow;

        // Check if exists and valid
        if (_locks.TryGetValue(key, out var expiration))
        {
            if (expiration > now) return new ValueTask<bool>(false);
        }

        // Add or Update
        _locks[key] = now.Add(ttl);
        return new ValueTask<bool>(true);
    }

    private void CleanupCycle(object? state)
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;

        foreach (var kvp in _locks)
        {
            if (kvp.Value < now) _locks.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}