using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Observability;
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
    private readonly int _fetchedJobTimeoutSeconds;
    private readonly int _processingZombieTimeoutSeconds;
    private readonly TimeSpan _scanInterval;

    public ZombieRescueService(
        IJobStorage storage,
        IChokaQNotifier notifier,
        ILogger<ZombieRescueService> logger,
        ChokaQOptions options)
    {
        _storage = storage;
        _notifier = notifier;
        _logger = logger;
        _fetchedJobTimeoutSeconds = options.FetchedJobTimeoutSeconds;
        _processingZombieTimeoutSeconds = options.ZombieTimeoutSeconds;
        _scanInterval = options.Recovery.ScanInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            ChokaQLogEvents.ZombieRescueStarted,
            "Zombie Rescue Service started. Watching for abandoned and dead jobs.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool statsChanged = false;

                // --- STEP 1: RECOVER ABANDONED JOBS ---
                // Fetched jobs are only reserved in a worker's local buffer; user code has not run yet.
                // This timeout is independent from the Processing zombie timeout because prefetch wait
                // time and execution heartbeat freshness are different operational signals.
                int abandonedRecovered = await _storage.RecoverAbandonedAsync(_fetchedJobTimeoutSeconds, stoppingToken);

                if (abandonedRecovered > 0)
                {
                    _logger.LogWarning(
                        ChokaQLogEvents.AbandonedJobsRecovered,
                        "RECOVERY: Found {Count} abandoned jobs (stuck in Fetched state). Returned them to Pending queue.",
                        abandonedRecovered);
                    statsChanged = true;
                }

                // --- STEP 2: ARCHIVE TRUE ZOMBIES ---
                // Jobs that were Processing but stopped sending heartbeats.
                // They get moved to the DLQ.
                int zombiesArchived = await _storage.ArchiveZombiesAsync(_processingZombieTimeoutSeconds, stoppingToken);

                if (zombiesArchived > 0)
                {
                    _logger.LogWarning(
                        ChokaQLogEvents.ZombieJobsArchived,
                        "ZOMBIE ALERT: Archived {Count} dead jobs to DLQ. They can be resurrected from the Morgue view.",
                        zombiesArchived);
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
                _logger.LogError(
                    ChokaQLogEvents.ZombieRescueCycleFailed,
                    ex,
                    "Failed to execute Zombie Rescue cycle.");
            }

            // The scan interval is configurable because recovery is an operational trade-off:
            // shorter scans reduce time-to-recovery, while longer scans reduce database load
            // for very large installations. Keeping it in ChokaQOptions makes that trade-off
            // visible instead of burying it in a magic constant.
            await Task.Delay(_scanInterval, stoppingToken);
        }
    }
}
