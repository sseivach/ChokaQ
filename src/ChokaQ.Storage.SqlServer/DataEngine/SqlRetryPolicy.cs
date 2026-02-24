using Microsoft.Data.SqlClient;

namespace ChokaQ.Storage.SqlServer.DataEngine;

/// <summary>
/// A zero-dependency retry policy for transient SQL Server errors (e.g., deadlocks, network blips).
/// Uses Exponential Backoff with Jitter to prevent thundering herd problems.
/// </summary>
internal static class SqlRetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        int maxRetries,
        int baseDelayMs,
        CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await action();
            }
            catch (SqlException ex) when (IsTransient(ex) && attempt < maxRetries)
            {
                attempt++;
                await Task.Delay(CalculateDelay(attempt, baseDelayMs), ct);
            }
        }
    }

    public static async Task ExecuteAsync(
        Func<Task> action,
        int maxRetries,
        int baseDelayMs,
        CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (SqlException ex) when (IsTransient(ex) && attempt < maxRetries)
            {
                attempt++;
                await Task.Delay(CalculateDelay(attempt, baseDelayMs), ct);
            }
        }
    }

    private static bool IsTransient(SqlException ex)
    {
        // Modern SqlClient handles most transient errors, but we explicitly add known codes
        if (ex.IsTransient) return true;

        foreach (SqlError error in ex.Errors)
        {
            if (error.Number == 1205 || // Deadlock
                error.Number == -2 ||   // Timeout
                error.Number == 4060 || // Cannot open database
                error.Number == 40197 || // Error processing request
                error.Number == 40501 || // Service busy
                error.Number == 40613)   // Database unavailable
            {
                return true;
            }
        }
        return false;
    }

    private static int CalculateDelay(int attempt, int baseDelayMs)
    {
        // Exponential backoff: base * 2^(attempt - 1)
        var backoff = baseDelayMs * Math.Pow(2, attempt - 1);

        // Jitter: Add 0 to 50% randomness to prevent multiple workers syncing up
        var jitter = Random.Shared.Next(0, (int)(baseDelayMs * 0.5));

        return (int)backoff + jitter;
    }
}