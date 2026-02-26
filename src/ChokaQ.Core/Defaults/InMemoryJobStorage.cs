using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// Thread-safe in-memory implementation of the Three Pillars storage.
/// Suitable for "Pipe Mode" (Rocket Speed) or local development.
/// 
/// Storage structure:
/// - _hotJobs: Active jobs (Pending, Fetched, Processing)
/// - _archiveJobs: Succeeded jobs (History)
/// - _dlqJobs: Failed/Cancelled/Zombie jobs (Dead Letter Queue)
/// - _stats: Pre-aggregated counters per queue
/// - _queues: Queue configuration
/// </summary>
public class InMemoryJobStorage : IJobStorage
{
    private readonly int _maxCapacity;
    private readonly object _transitionLock = new();

    // Three Pillars
    private readonly ConcurrentDictionary<string, JobHotEntity> _hotJobs = new();
    private readonly ConcurrentDictionary<string, JobArchiveEntity> _archiveJobs = new();
    private readonly ConcurrentDictionary<string, JobDLQEntity> _dlqJobs = new();

    // Aggregates & Config
    private readonly ConcurrentDictionary<string, StatsSummaryData> _stats = new();
    private readonly ConcurrentDictionary<string, QueueData> _queues = new();

    public InMemoryJobStorage(InMemoryStorageOptions options)
    {
        _maxCapacity = options.MaxCapacity;

        // Initialize default queue
        _queues.TryAdd("default", new QueueData("default"));
        _stats.TryAdd("default", new StatsSummaryData("default"));
    }

    // ========================================================================
    // CORE OPERATIONS (Hot Table)
    // ========================================================================

    public ValueTask<string> EnqueueAsync(
        string id,
        string queue,
        string jobType,
        string payload,
        int priority = 10,
        string? createdBy = null,
        string? tags = null,
        TimeSpan? delay = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        // Idempotency check
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = _hotJobs.Values.FirstOrDefault(j => j.IdempotencyKey == idempotencyKey);
            if (existing != null)
                return new ValueTask<string>(existing.Id);
        }

        var now = DateTime.UtcNow;
        var job = new JobHotEntity(
            Id: id,
            Queue: queue,
            Type: jobType,
            Payload: payload,
            Tags: tags,
            IdempotencyKey: idempotencyKey,
            Priority: priority,
            Status: JobStatus.Pending,
            AttemptCount: 0,
            WorkerId: null,
            HeartbeatUtc: null,
            ScheduledAtUtc: delay.HasValue ? now.Add(delay.Value) : null,
            CreatedAtUtc: now,
            StartedAtUtc: null,
            LastUpdatedUtc: now,
            CreatedBy: createdBy,
            LastModifiedBy: null
        );

        EnforceCapacity();
        _hotJobs[id] = job;
        EnsureQueueExists(queue);

        return new ValueTask<string>(id);
    }

    public ValueTask<IEnumerable<JobHotEntity>> FetchNextBatchAsync(
        string workerId,
        int batchSize,
        string[]? allowedQueues = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var candidates = _hotJobs.Values
            .Where(j => j.Status == JobStatus.Pending &&
                        (allowedQueues == null || allowedQueues.Contains(j.Queue)) &&
                        (!j.ScheduledAtUtc.HasValue || j.ScheduledAtUtc <= now) &&
                        !IsQueuePaused(j.Queue))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.ScheduledAtUtc ?? j.CreatedAtUtc)
            .Take(batchSize)
            .ToList();

        var lockedJobs = new List<JobHotEntity>();

        foreach (var job in candidates)
        {
            var fetched = job with
            {
                Status = JobStatus.Fetched,
                WorkerId = workerId,
                AttemptCount = job.AttemptCount + 1,
                LastUpdatedUtc = now
            };

            // Optimistic concurrency
            if (_hotJobs.TryUpdate(job.Id, fetched, job))
            {
                lockedJobs.Add(fetched);
            }
        }

        return new ValueTask<IEnumerable<JobHotEntity>>(lockedJobs);
    }

    public ValueTask MarkAsProcessingAsync(string jobId, CancellationToken ct = default)
    {
        if (_hotJobs.TryGetValue(jobId, out var job))
        {
            var now = DateTime.UtcNow;
            var processing = job with
            {
                Status = JobStatus.Processing,
                StartedAtUtc = now,
                HeartbeatUtc = now,
                LastUpdatedUtc = now
            };
            _hotJobs.TryUpdate(jobId, processing, job);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default)
    {
        if (_hotJobs.TryGetValue(jobId, out var job))
        {
            var now = DateTime.UtcNow;
            var updated = job with { HeartbeatUtc = now, LastUpdatedUtc = now };
            _hotJobs.TryUpdate(jobId, updated, job);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<JobHotEntity?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        _hotJobs.TryGetValue(jobId, out var job);
        return new ValueTask<JobHotEntity?>(job);
    }

    // ========================================================================
    // ATOMIC TRANSITIONS (Three Pillars)
    // ========================================================================

    public ValueTask ArchiveSucceededAsync(string jobId, double? durationMs = null, CancellationToken ct = default)
    {
        lock (_transitionLock)
        {
            if (!_hotJobs.TryRemove(jobId, out var job))
                return ValueTask.CompletedTask;

            var now = DateTime.UtcNow;
            var archived = new JobArchiveEntity(
                Id: job.Id,
                Queue: job.Queue,
                Type: job.Type,
                Payload: job.Payload,
                Tags: job.Tags,
                AttemptCount: job.AttemptCount,
                WorkerId: job.WorkerId,
                CreatedBy: job.CreatedBy,
                LastModifiedBy: job.LastModifiedBy,
                CreatedAtUtc: job.CreatedAtUtc,
                StartedAtUtc: job.StartedAtUtc,
                FinishedAtUtc: now,
                DurationMs: durationMs ?? (job.StartedAtUtc.HasValue
                    ? (now - job.StartedAtUtc.Value).TotalMilliseconds
                    : null)
            );

            _archiveJobs[job.Id] = archived;
            IncrementStats(job.Queue, succeeded: 1);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ArchiveFailedAsync(string jobId, string errorDetails, CancellationToken ct = default)
    {
        return MoveToDLQ(jobId, FailureReason.MaxRetriesExceeded, errorDetails);
    }

    public ValueTask ArchiveCancelledAsync(string jobId, string? cancelledBy = null, CancellationToken ct = default)
    {
        var error = cancelledBy != null
            ? $"Cancelled by admin: {cancelledBy}"
            : "Cancelled by admin";
        return MoveToDLQ(jobId, FailureReason.Cancelled, error);
    }

    public ValueTask<int> ArchiveCancelledBatchAsync(string[] jobIds, string? cancelledBy = null, CancellationToken ct = default)
    {
        int count = 0;
        var error = cancelledBy != null ? $"Cancelled by: {cancelledBy}" : "Cancelled by admin (Batch)";

        foreach (var id in jobIds)
        {
            lock (_transitionLock)
            {
                if (_hotJobs.TryGetValue(id, out var job) &&
                   (job.Status == JobStatus.Pending || job.Status == JobStatus.Fetched))
                {
                    MoveToDLQ(id, FailureReason.Cancelled, error).GetAwaiter().GetResult();
                    count++;
                }
            }
        }
        return new ValueTask<int>(count);
    }

    public ValueTask ArchiveZombieAsync(string jobId, CancellationToken ct = default)
    {
        return MoveToDLQ(jobId, FailureReason.Zombie, "Zombie: Worker heartbeat expired");
    }

    public ValueTask ResurrectAsync(
        string jobId,
        JobDataUpdateDto? updates = null,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        lock (_transitionLock)
        {
            if (!_dlqJobs.TryRemove(jobId, out var dlqJob))
                return ValueTask.CompletedTask;

            var now = DateTime.UtcNow;
            var resurrected = new JobHotEntity(
                Id: dlqJob.Id,
                Queue: dlqJob.Queue,
                Type: dlqJob.Type,
                Payload: updates?.Payload ?? dlqJob.Payload,
                Tags: updates?.Tags ?? dlqJob.Tags,
                IdempotencyKey: null, // Clear idempotency on resurrection
                Priority: updates?.Priority ?? 10,
                Status: JobStatus.Pending,
                AttemptCount: 0, // Reset attempts
                WorkerId: null,
                HeartbeatUtc: null,
                ScheduledAtUtc: null,
                CreatedAtUtc: dlqJob.CreatedAtUtc,
                StartedAtUtc: null,
                LastUpdatedUtc: now,
                CreatedBy: dlqJob.CreatedBy,
                LastModifiedBy: resurrectedBy
            );

            _hotJobs[dlqJob.Id] = resurrected;
            DecrementStats(dlqJob.Queue, failed: 1);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ResurrectBatchAsync(string[] jobIds, string? resurrectedBy = null, CancellationToken ct = default)
    {
        int count = 0;
        // Process in batches of 1000
        foreach (var batch in jobIds.Chunk(1000))
        {
            foreach (var id in batch)
            {
                lock (_transitionLock)
                {
                    if (_dlqJobs.TryRemove(id, out var dlqJob))
                    {
                        var now = DateTime.UtcNow;
                        var resurrected = new JobHotEntity(
                            Id: dlqJob.Id,
                            Queue: dlqJob.Queue,
                            Type: dlqJob.Type,
                            Payload: dlqJob.Payload,
                            Tags: dlqJob.Tags,
                            IdempotencyKey: null,
                            Priority: 10,
                            Status: JobStatus.Pending,
                            AttemptCount: 0,
                            WorkerId: null,
                            HeartbeatUtc: null,
                            ScheduledAtUtc: null,
                            CreatedAtUtc: dlqJob.CreatedAtUtc,
                            StartedAtUtc: null,
                            LastUpdatedUtc: now,
                            CreatedBy: dlqJob.CreatedBy,
                            LastModifiedBy: resurrectedBy
                        );

                        _hotJobs[dlqJob.Id] = resurrected;
                        DecrementStats(dlqJob.Queue, failed: 1);
                        count++;
                    }
                }
            }
        }
        return new ValueTask<int>(count);
    }

    public ValueTask ReleaseJobAsync(string jobId, CancellationToken ct = default)
    {
        // Revert status to Pending so other workers/threads can pick it up.
        if (_hotJobs.TryGetValue(jobId, out var job))
        {
            var released = job with
            {
                Status = JobStatus.Pending,
                WorkerId = null,
                AttemptCount = Math.Max(0, job.AttemptCount - 1),
                LastUpdatedUtc = DateTime.UtcNow
            };
            _hotJobs.TryUpdate(jobId, released, job);
        }
        return ValueTask.CompletedTask;
    }

    // ========================================================================
    // RETRY LOGIC (Stays in Hot)
    // ========================================================================

    public ValueTask RescheduleForRetryAsync(
        string jobId,
        DateTime scheduledAtUtc,
        int newAttemptCount,
        string lastError,
        CancellationToken ct = default)
    {
        if (_hotJobs.TryGetValue(jobId, out var job))
        {
            var rescheduled = job with
            {
                Status = JobStatus.Pending,
                ScheduledAtUtc = scheduledAtUtc,
                AttemptCount = newAttemptCount,
                WorkerId = null,
                HeartbeatUtc = null,
                LastUpdatedUtc = DateTime.UtcNow
            };
            _hotJobs.TryUpdate(jobId, rescheduled, job);
            IncrementStats(job.Queue, retried: 1);
        }
        return ValueTask.CompletedTask;
    }

    // ========================================================================
    // DIVINE MODE (Admin Operations)
    // ========================================================================

    public ValueTask<bool> UpdateJobDataAsync(
        string jobId,
        JobDataUpdateDto updates,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        if (!_hotJobs.TryGetValue(jobId, out var job))
            return new ValueTask<bool>(false);

        // Safety Gate: Only Pending jobs can be edited
        if (job.Status != JobStatus.Pending)
            return new ValueTask<bool>(false);

        var updated = job with
        {
            Payload = updates.Payload ?? job.Payload,
            Tags = updates.Tags ?? job.Tags,
            Priority = updates.Priority ?? job.Priority,
            LastModifiedBy = modifiedBy,
            LastUpdatedUtc = DateTime.UtcNow
        };

        return new ValueTask<bool>(_hotJobs.TryUpdate(jobId, updated, job));
    }

    public ValueTask PurgeDLQAsync(string[] jobIds, CancellationToken ct = default)
    {
        foreach (var id in jobIds)
        {
            _dlqJobs.TryRemove(id, out _);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> PurgeArchiveAsync(DateTime olderThan, CancellationToken ct = default)
    {
        var toRemove = _archiveJobs.Values
            .Where(j => j.FinishedAtUtc < olderThan)
            .Select(j => j.Id)
            .ToList();

        foreach (var id in toRemove)
        {
            _archiveJobs.TryRemove(id, out _);
        }

        return new ValueTask<int>(toRemove.Count);
    }

    public ValueTask<bool> UpdateDLQJobDataAsync(
        string jobId,
        JobDataUpdateDto updates,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        if (!_dlqJobs.TryGetValue(jobId, out var job))
        {
            return new ValueTask<bool>(false);
        }

        var updatedJob = job with
        {
            Payload = updates.Payload ?? job.Payload,
            Tags = updates.Tags ?? job.Tags,
            LastModifiedBy = modifiedBy
        };

        var success = _dlqJobs.TryUpdate(jobId, updatedJob, job);

        return new ValueTask<bool>(success);
    }

    // ========================================================================
    // OBSERVABILITY (Dashboard)
    // ========================================================================

    public ValueTask<StatsSummaryEntity> GetSummaryStatsAsync(CancellationToken ct = default)
    {
        // Hybrid: Real-time counts from Hot + totals from Stats
        var hotValues = _hotJobs.Values;
        var statsValues = _stats.Values;

        var stats = new StatsSummaryEntity(
            Queue: null, // Aggregated across all queues
            Pending: hotValues.Count(x => x.Status == JobStatus.Pending),
            Fetched: hotValues.Count(x => x.Status == JobStatus.Fetched),
            Processing: hotValues.Count(x => x.Status == JobStatus.Processing),
            SucceededTotal: statsValues.Sum(x => x.SucceededTotal),
            FailedTotal: statsValues.Sum(x => x.FailedTotal),
            RetriedTotal: statsValues.Sum(x => x.RetriedTotal),
            Total: hotValues.Count + _archiveJobs.Count + _dlqJobs.Count,
            LastActivityUtc: statsValues.Max(x => x.LastActivityUtc)
        );

        return new ValueTask<StatsSummaryEntity>(stats);
    }

    public ValueTask<IEnumerable<StatsSummaryEntity>> GetQueueStatsAsync(CancellationToken ct = default)
    {
        // Per-queue stats
        var allQueues = _queues.Keys.ToList();
        var result = new List<StatsSummaryEntity>();

        foreach (var queueName in allQueues)
        {
            var hotForQueue = _hotJobs.Values.Where(x => x.Queue == queueName).ToList();
            var statForQueue = _stats.GetValueOrDefault(queueName);

            var queueStat = new StatsSummaryEntity(
                Queue: queueName,
                Pending: hotForQueue.Count(x => x.Status == JobStatus.Pending),
                Fetched: hotForQueue.Count(x => x.Status == JobStatus.Fetched),
                Processing: hotForQueue.Count(x => x.Status == JobStatus.Processing),
                SucceededTotal: statForQueue?.SucceededTotal ?? 0,
                FailedTotal: statForQueue?.FailedTotal ?? 0,
                RetriedTotal: statForQueue?.RetriedTotal ?? 0,
                Total: hotForQueue.Count +
                       _archiveJobs.Values.Count(x => x.Queue == queueName) +
                       _dlqJobs.Values.Count(x => x.Queue == queueName),
                LastActivityUtc: statForQueue?.LastActivityUtc
            );

            result.Add(queueStat);
        }

        return new ValueTask<IEnumerable<StatsSummaryEntity>>(result);
    }

    public ValueTask<IEnumerable<JobHotEntity>> GetActiveJobsAsync(
        int limit = 100,
        JobStatus? statusFilter = null,
        string? queueFilter = null,
        string? searchTerm = null,
        CancellationToken ct = default)
    {
        var query = _hotJobs.Values.AsEnumerable();

        if (statusFilter.HasValue)
            query = query.Where(j => j.Status == statusFilter.Value);

        if (!string.IsNullOrEmpty(queueFilter))
            query = query.Where(j => j.Queue == queueFilter);

        if (!string.IsNullOrEmpty(searchTerm))
            query = query.Where(j =>
                j.Id.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                j.Type.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                (j.Tags?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false));

        var result = query
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(limit)
            .ToList();

        return new ValueTask<IEnumerable<JobHotEntity>>(result);
    }

    public ValueTask<IEnumerable<JobArchiveEntity>> GetArchiveJobsAsync(
        int limit = 100,
        string? queueFilter = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? tagFilter = null,
        CancellationToken ct = default)
    {
        var query = _archiveJobs.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(queueFilter))
            query = query.Where(j => j.Queue == queueFilter);

        if (fromDate.HasValue)
            query = query.Where(j => j.FinishedAtUtc >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(j => j.FinishedAtUtc <= toDate.Value);

        if (!string.IsNullOrEmpty(tagFilter))
            query = query.Where(j => j.Tags?.Contains(tagFilter, StringComparison.OrdinalIgnoreCase) ?? false);

        var result = query
            .OrderByDescending(j => j.FinishedAtUtc)
            .Take(limit)
            .ToList();

        return new ValueTask<IEnumerable<JobArchiveEntity>>(result);
    }

    public ValueTask<IEnumerable<JobDLQEntity>> GetDLQJobsAsync(
        int limit = 100,
        string? queueFilter = null,
        FailureReason? reasonFilter = null,
        string? searchTerm = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var query = _dlqJobs.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(queueFilter))
            query = query.Where(j => j.Queue == queueFilter);

        if (reasonFilter.HasValue)
            query = query.Where(j => j.FailureReason == reasonFilter.Value);

        if (fromDate.HasValue)
            query = query.Where(j => j.CreatedAtUtc >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(j => j.CreatedAtUtc <= toDate.Value);

        if (!string.IsNullOrEmpty(searchTerm))
            query = query.Where(j =>
                j.Id.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                j.Type.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                (j.Tags?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (j.ErrorDetails?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false));

        var result = query
            .OrderByDescending(j => j.FailedAtUtc)
            .Take(limit)
            .ToList();

        return new ValueTask<IEnumerable<JobDLQEntity>>(result);
    }

    public ValueTask<JobArchiveEntity?> GetArchiveJobAsync(string jobId, CancellationToken ct = default)
    {
        _archiveJobs.TryGetValue(jobId, out var job);
        return new ValueTask<JobArchiveEntity?>(job);
    }

    public ValueTask<JobDLQEntity?> GetDLQJobAsync(string jobId, CancellationToken ct = default)
    {
        _dlqJobs.TryGetValue(jobId, out var job);
        return new ValueTask<JobDLQEntity?>(job);
    }

    // ========================================================================
    // QUEUE MANAGEMENT
    // ========================================================================

    public ValueTask<IEnumerable<QueueEntity>> GetQueuesAsync(CancellationToken ct = default)
    {
        // Collect all unique queues from all three pillars
        var allQueues = _hotJobs.Values.Select(j => j.Queue)
            .Concat(_archiveJobs.Values.Select(j => j.Queue))
            .Concat(_dlqJobs.Values.Select(j => j.Queue))
            .Concat(_queues.Keys)
            .Distinct();

        var result = allQueues.Select(queueName =>
        {
            var queueData = _queues.GetOrAdd(queueName, new QueueData(queueName));
            return new QueueEntity(
                Name: queueName,
                IsPaused: queueData.IsPaused,
                IsActive: queueData.IsActive,
                ZombieTimeoutSeconds: queueData.ZombieTimeoutSeconds,
                LastUpdatedUtc: queueData.LastUpdatedUtc
            );
        }).ToList();

        return new ValueTask<IEnumerable<QueueEntity>>(result);
    }

    public ValueTask SetQueuePausedAsync(string queueName, bool isPaused, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueData(queueName) { IsPaused = isPaused },
            (_, old) => { old.IsPaused = isPaused; old.LastUpdatedUtc = DateTime.UtcNow; return old; });
        return ValueTask.CompletedTask;
    }

    public ValueTask SetQueueZombieTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueData(queueName) { ZombieTimeoutSeconds = timeoutSeconds },
            (_, old) => { old.ZombieTimeoutSeconds = timeoutSeconds; old.LastUpdatedUtc = DateTime.UtcNow; return old; });
        return ValueTask.CompletedTask;
    }

    public ValueTask SetQueueActiveAsync(string queueName, bool isActive, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueData(queueName) { IsActive = isActive },
            (_, old) => { old.IsActive = isActive; old.LastUpdatedUtc = DateTime.UtcNow; return old; });
        return ValueTask.CompletedTask;
    }

    // ========================================================================
    // RECOVERY & ZOMBIE DETECTION
    // ========================================================================

    public ValueTask<int> RecoverAbandonedAsync(int timeoutSeconds, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        int count = 0;

        var abandonedJobs = _hotJobs.Values
            .Where(j => j.Status == JobStatus.Fetched && j.LastUpdatedUtc < now.AddSeconds(-timeoutSeconds))
            .ToList();

        foreach (var job in abandonedJobs)
        {
            lock (_transitionLock)
            {
                var recovered = job with
                {
                    Status = JobStatus.Pending,
                    WorkerId = null,
                    AttemptCount = Math.Max(0, job.AttemptCount - 1),
                    LastUpdatedUtc = now
                };

                if (_hotJobs.TryUpdate(job.Id, recovered, job))
                {
                    count++;
                }
            }
        }
        return new ValueTask<int>(count);
    }

    public ValueTask<int> ArchiveZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        int count = 0;

        // In-memory engine already ignores Fetched here, so this is purely for Processing jobs
        var processingJobs = _hotJobs.Values
            .Where(j => j.Status == JobStatus.Processing)
            .ToList();

        foreach (var job in processingJobs)
        {
            // Determine effective timeout
            int timeout = globalTimeoutSeconds;
            if (_queues.TryGetValue(job.Queue, out var q) && q.ZombieTimeoutSeconds.HasValue)
                timeout = q.ZombieTimeoutSeconds.Value;

            // Check heartbeat expiration
            var heartbeat = job.HeartbeatUtc ?? job.StartedAtUtc ?? job.LastUpdatedUtc;
            if (heartbeat < now.AddSeconds(-timeout))
            {
                // Move to DLQ
                lock (_transitionLock)
                {
                    if (_hotJobs.TryRemove(job.Id, out var removed))
                    {
                        var dlqJob = new JobDLQEntity(
                            Id: removed.Id,
                            Queue: removed.Queue,
                            Type: removed.Type,
                            Payload: removed.Payload,
                            Tags: removed.Tags,
                            FailureReason: FailureReason.Zombie,
                            ErrorDetails: $"Zombie: Heartbeat expired after {timeout}s",
                            AttemptCount: removed.AttemptCount,
                            WorkerId: removed.WorkerId,
                            CreatedBy: removed.CreatedBy,
                            LastModifiedBy: removed.LastModifiedBy,
                            CreatedAtUtc: removed.CreatedAtUtc,
                            FailedAtUtc: now
                        );

                        _dlqJobs[removed.Id] = dlqJob;
                        IncrementStats(removed.Queue, failed: 1);
                        count++;
                    }
                }
            }
        }

        return new ValueTask<int>(count);
    }

    // ========================================================================
    // HISTORY
    // ========================================================================

    public ValueTask<PagedResult<JobArchiveEntity>> GetArchivePagedAsync(
        HistoryFilterDto filter,
        CancellationToken ct = default)
    {
        var query = _archiveJobs.Values.AsEnumerable();

        // 1. Filter
        if (!string.IsNullOrEmpty(filter.Queue))
            query = query.Where(x => x.Queue == filter.Queue);

        if (filter.FromUtc.HasValue)
            query = query.Where(x => x.FinishedAtUtc >= filter.FromUtc);

        if (filter.ToUtc.HasValue)
            query = query.Where(x => x.FinishedAtUtc <= filter.ToUtc);

        if (!string.IsNullOrEmpty(filter.SearchTerm))
            query = query.Where(x => x.Id.Contains(filter.SearchTerm, StringComparison.OrdinalIgnoreCase) ||
                                     x.Type.Contains(filter.SearchTerm, StringComparison.OrdinalIgnoreCase));

        // 2. Sort
        if (filter.SortDescending)
            query = query.OrderByDescending(x => x.FinishedAtUtc); // Simplified sort
        else
            query = query.OrderBy(x => x.FinishedAtUtc);

        var total = query.Count();

        // 3. Page
        var items = query
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToList();

        return new ValueTask<PagedResult<JobArchiveEntity>>(
            new PagedResult<JobArchiveEntity>(items, total, filter.PageNumber, filter.PageSize));
    }

    public ValueTask<PagedResult<JobDLQEntity>> GetDLQPagedAsync(
        HistoryFilterDto filter,
        CancellationToken ct = default)
    {
        var query = _dlqJobs.Values.AsEnumerable();

        // Similar filters...
        if (!string.IsNullOrEmpty(filter.Queue))
            query = query.Where(x => x.Queue == filter.Queue);

        if (filter.FromUtc.HasValue)
            query = query.Where(x => x.CreatedAtUtc >= filter.FromUtc); // DLQ usually uses Created or Failed

        if (!string.IsNullOrEmpty(filter.SearchTerm))
            query = query.Where(x => x.Id.Contains(filter.SearchTerm, StringComparison.OrdinalIgnoreCase));

        var total = query.Count();

        var items = query
            .OrderByDescending(x => x.FailedAtUtc)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToList();

        return new ValueTask<PagedResult<JobDLQEntity>>(
            new PagedResult<JobDLQEntity>(items, total, filter.PageNumber, filter.PageSize));
    }

    // ========================================================================
    // PRIVATE HELPERS
    // ========================================================================

    private ValueTask MoveToDLQ(string jobId, FailureReason reason, string errorDetails)
    {
        lock (_transitionLock)
        {
            if (!_hotJobs.TryRemove(jobId, out var job))
                return ValueTask.CompletedTask;

            var now = DateTime.UtcNow;
            var dlqJob = new JobDLQEntity(
                Id: job.Id,
                Queue: job.Queue,
                Type: job.Type,
                Payload: job.Payload,
                Tags: job.Tags,
                FailureReason: reason,
                ErrorDetails: errorDetails,
                AttemptCount: job.AttemptCount,
                WorkerId: job.WorkerId,
                CreatedBy: job.CreatedBy,
                LastModifiedBy: job.LastModifiedBy,
                CreatedAtUtc: job.CreatedAtUtc,
                FailedAtUtc: now
            );

            _dlqJobs[job.Id] = dlqJob;
            IncrementStats(job.Queue, failed: 1);
        }
        return ValueTask.CompletedTask;
    }

    private void EnsureQueueExists(string queue)
    {
        _queues.TryAdd(queue, new QueueData(queue));
        _stats.TryAdd(queue, new StatsSummaryData(queue));
    }

    private bool IsQueuePaused(string queue)
    {
        return _queues.TryGetValue(queue, out var q) && q.IsPaused;
    }

    private void IncrementStats(string queue, long succeeded = 0, long failed = 0, long retried = 0)
    {
        _stats.AddOrUpdate(queue,
            new StatsSummaryData(queue)
            {
                SucceededTotal = succeeded,
                FailedTotal = failed,
                RetriedTotal = retried
            },
            (_, old) =>
            {
                old.SucceededTotal += succeeded;
                old.FailedTotal += failed;
                old.RetriedTotal += retried;
                old.LastActivityUtc = DateTime.UtcNow;
                return old;
            });
    }

    private void DecrementStats(string queue, long failed = 0)
    {
        if (_stats.TryGetValue(queue, out var stats))
        {
            stats.FailedTotal = Math.Max(0, stats.FailedTotal - failed);
            stats.LastActivityUtc = DateTime.UtcNow;
        }
    }

    private void EnforceCapacity()
    {
        var totalCount = _hotJobs.Count + _archiveJobs.Count + _dlqJobs.Count;

        if (totalCount < _maxCapacity)
            return;

        // First: Remove old archived jobs
        var archiveToRemove = _archiveJobs.Values
            .OrderBy(j => j.FinishedAtUtc)
            .Take(1000)
            .Select(j => j.Id)
            .ToList();

        foreach (var id in archiveToRemove)
            _archiveJobs.TryRemove(id, out _);

        // If still over capacity: Remove old DLQ jobs
        if (_hotJobs.Count + _archiveJobs.Count + _dlqJobs.Count >= _maxCapacity)
        {
            var dlqToRemove = _dlqJobs.Values
                .OrderBy(j => j.FailedAtUtc)
                .Take(500)
                .Select(j => j.Id)
                .ToList();

            foreach (var id in dlqToRemove)
                _dlqJobs.TryRemove(id, out _);
        }
    }

    // ========================================================================
    // INTERNAL DATA CLASSES
    // ========================================================================

    private class QueueData
    {
        public string Name { get; }
        public bool IsPaused { get; set; }
        public bool IsActive { get; set; } = true;
        public int? ZombieTimeoutSeconds { get; set; }
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

        public QueueData(string name) => Name = name;
    }

    private class StatsSummaryData
    {
        public string Queue { get; }
        public long SucceededTotal { get; set; }
        public long FailedTotal { get; set; }
        public long RetriedTotal { get; set; }
        public DateTime? LastActivityUtc { get; set; }

        public StatsSummaryData(string queue) => Queue = queue;
    }
}