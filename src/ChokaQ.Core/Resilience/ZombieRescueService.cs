using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Resilience;

/// <summary>
/// Background service that periodically scans for stuck jobs.
/// 
/// Step 1 (Recovery): Finds "Abandoned" jobs (stuck in Fetched state due to a worker crash before processing started) 
/// and returns them to the queue (Pending) for another worker to pick up.
/// 
/// Step 2 (Zombie Kill): Finds "Zombie" jobs (stuck in Processing state with an expired heartbeat).
/// In Three Pillars architecture, zombies are moved to DLQ with FailureReason.Zombie.
/// </summary>
public class ZombieRescueService : BackgroundService
{
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly ILogger<ZombieRescueService> _logger;
    private readonly int _globalZombieTimeoutSeconds;

    // Config: How often to run the cleanup scan
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    public ZombieRescueService(
        IJobStorage storage,
        IChokaQNotifier notifier,
        ILogger<ZombieRescueService> logger,
        ChokaQOptions options)
    {
        _storage = storage;
        _notifier = notifier;
        _logger = logger;
        _globalZombieTimeoutSeconds = options.ZombieTimeoutSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Zombie Rescue Service started. Watching for abandoned and dead jobs...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool statsChanged = false;

                // --- STEP 1: RECOVER ABANDONED JOBS ---
                // Jobs that were Fetched but never made it to Processing.
                // We use the same timeout setting for simplicity. They get returned to Pending (Status 0).
                int abandonedRecovered = await _storage.RecoverAbandonedAsync(_globalZombieTimeoutSeconds, stoppingToken);

                if (abandonedRecovered > 0)
                {
                    _logger.LogWarning("RECOVERY: Found {Count} abandoned jobs (stuck in Fetched state). Returned them to Pending queue.", abandonedRecovered);
                    statsChanged = true;
                }

                // --- STEP 2: ARCHIVE TRUE ZOMBIES ---
                // Jobs that were Processing but stopped sending heartbeats.
                // They get moved to the DLQ.
                int zombiesArchived = await _storage.ArchiveZombiesAsync(_globalZombieTimeoutSeconds, stoppingToken);

                if (zombiesArchived > 0)
                {
                    _logger.LogWarning("ZOMBIE ALERT: Archived {Count} dead jobs to DLQ. They can be resurrected from the Morgue view.", zombiesArchived);
                    statsChanged = true;
                }

                // --- STEP 3: NOTIFY UI ---
                if (statsChanged)
                {
                    try
                    {
                        await _notifier.NotifyStatsUpdatedAsync();
                    }
                    catch
                    {
                        // Ignore notification failures - UI will eventually catch up
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