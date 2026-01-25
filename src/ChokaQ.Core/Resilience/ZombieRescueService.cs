using ChokaQ.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Resilience;

/// <summary>
/// Background service that periodically scans for "Zombie" jobs.
/// A Zombie is a job stuck in 'Processing' state but whose worker has stopped sending heartbeats
/// (e.g., due to a server crash, power outage, or OOM killer).
/// </summary>
public class ZombieRescueService : BackgroundService
{
    private readonly IJobStorage _storage;
    private readonly ILogger<ZombieRescueService> _logger;

    // Config: How often to run the cleanup scan
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    // Config: Default fallback timeout if queue specific setting is missing.
    // 10 minutes allows for some network latency or long GC pauses before declaring death.
    private const int DefaultGlobalZombieTimeoutSeconds = 600;

    public ZombieRescueService(IJobStorage storage, ILogger<ZombieRescueService> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Zombie Rescue Service started. Watching for dead workers...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Execute the cleanup. 
                // The storage implementation handles the Distributed Lock (if SQL) 
                // and the specific timeout logic per queue.
                int zombiesFound = await _storage.MarkZombiesAsync(DefaultGlobalZombieTimeoutSeconds, stoppingToken);

                if (zombiesFound > 0)
                {
                    _logger.LogWarning("ZOMBIE ALERT: Found and marked {Count} dead jobs. They require manual intervention (Restart) or investigation.", zombiesFound);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute Zombie Rescue cycle.");
            }

            // Wait for next cycle
            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}