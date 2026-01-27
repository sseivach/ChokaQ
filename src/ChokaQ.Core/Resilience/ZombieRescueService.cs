using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Resilience;

/// <summary>
/// Background service that periodically scans for "Zombie" jobs.
/// A Zombie is a job stuck in 'Processing' state but whose worker has stopped sending heartbeats
/// (e.g., due to a server crash, power outage, or OOM killer).
/// 
/// In Three Pillars architecture, zombies are moved to DLQ with FailureReason.Zombie.
/// </summary>
public class ZombieRescueService : BackgroundService
{
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly ILogger<ZombieRescueService> _logger;

    // Config: How often to run the cleanup scan
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    // Config: Default fallback timeout if queue specific setting is missing.
    // 10 minutes allows for some network latency or long GC pauses before declaring death.
    private const int DefaultGlobalZombieTimeoutSeconds = 600;

    public ZombieRescueService(
        IJobStorage storage,
        IChokaQNotifier notifier,
        ILogger<ZombieRescueService> logger)
    {
        _storage = storage;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Zombie Rescue Service started. Watching for dead workers...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Execute the cleanup - zombies are moved to DLQ
                int zombiesArchived = await _storage.ArchiveZombiesAsync(DefaultGlobalZombieTimeoutSeconds, stoppingToken);

                if (zombiesArchived > 0)
                {
                    _logger.LogWarning(
                        "ZOMBIE ALERT: Archived {Count} dead jobs to DLQ. They can be resurrected from the Morgue view.",
                        zombiesArchived);

                    // Notify dashboard to refresh
                    try
                    {
                        await _notifier.NotifyStatsUpdatedAsync();
                    }
                    catch
                    {
                        // Ignore notification failures
                    }
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
