using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// Thread-safe in-memory implementation of the Three Pillars storage.
/// Suitable for "Pipe Mode" (Rocket Speed) or local development.
/// </summary>
public class InMemoryJobStorage : IJobStorage
{
    private const int ErrorPrefixLength = 160;
    private const int MetricOutcomeSucceeded = 0;
    private const int MetricOutcomeFailed = 1;
    private const int NoFailureReason = -1;

    private readonly int _maxCapacity;
    private readonly object _transitionLock = new();

    // Three Pillars
    private readonly ConcurrentDictionary<string, JobHotEntity> _hotJobs = new();
    private readonly ConcurrentDictionary<string, JobArchiveEntity> _archiveJobs = new();
    private readonly ConcurrentDictionary<string, JobDLQEntity> _dlqJobs = new();

    // Aggregates & Config
    private readonly ConcurrentDictionary<string, StatsSummaryData> _stats = new();
    private readonly ConcurrentDictionary<string, QueueData> _queues = new();
    private readonly ConcurrentDictionary<MetricBucketKey, MetricBucketData> _metricBuckets = new();

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
        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey);

        if (!string.IsNullOrEmpty(normalizedIdempotencyKey))
        {
            // ChokaQ's built-in enqueue dedupe is intentionally scoped to the Hot pillar.
            // Archive and DLQ keep historical/operator records, but they are not admission-control
            // indexes. Once work leaves Hot, the same business key may be submitted again.
            var existing = _hotJobs.Values.FirstOrDefault(j => j.IdempotencyKey == normalizedIdempotencyKey);
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
            IdempotencyKey: normalizedIdempotencyKey,
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

    /// <summary>
    /// Atomically fetches and locks the next batch of pending jobs.
    /// NOTE: This uses an O(N) LINQ scan over the hot dictionary. While a PriorityQueue (O(log N)) 
    /// seems better, it introduces severe Head-of-Line blocking issues with Paused Queues and 
    /// Scheduled jobs (requiring constant dequeue/re-enqueue spinning). 
    /// Since the primary In-Memory execution path uses System.Threading.Channels (O(1)) instead 
    /// of this polling method, this LINQ approach is kept for safety and simplicity in custom polling scenarios.
    /// </summary>
    public ValueTask<IEnumerable<JobHotEntity>> FetchNextBatchAsync(
        string workerId,
        int batchSize,
        string[]? allowedQueues = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Calculate active counts per queue for Bulkhead pattern (MaxWorkers)
        var activeCounts = _hotJobs.Values
            .Where(j => j.Status == JobStatus.Fetched || j.Status == JobStatus.Processing)
            .GroupBy(j => j.Queue)
            .ToDictionary(g => g.Key, g => g.Count());

        var candidates = _hotJobs.Values
            .Where(j => j.Status == JobStatus.Pending &&
                        (allowedQueues == null || allowedQueues.Contains(j.Queue)) &&
                        (!j.ScheduledAtUtc.HasValue || j.ScheduledAtUtc <= now) &&
                        !IsQueuePaused(j.Queue) &&
                        !IsQueueLimitReached(j.Queue, activeCounts))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.ScheduledAtUtc ?? j.CreatedAtUtc)
            .Take(batchSize)
            .ToList();

        var lockedJobs = new List<JobHotEntity>();

        foreach (var job in candidates)
        {
            if (IsQueueLimitReached(job.Queue, activeCounts))
            {
                // The candidate list is built from a point-in-time view. Re-checking the
                // per-queue bulkhead inside the update loop prevents one large fetch batch
                // from overshooting MaxWorkers after earlier rows in this same batch claimed slots.
                continue;
            }

            var fetched = job with
            {
                Status = JobStatus.Fetched,
                WorkerId = workerId,
                LastUpdatedUtc = now
            };

            // Optimistic concurrency
            if (_hotJobs.TryUpdate(job.Id, fetched, job))
            {
                lockedJobs.Add(fetched);

                // Update local active count so we don't exceed limit within the same batch fetch
                if (activeCounts.ContainsKey(job.Queue))
                    activeCounts[job.Queue]++;
                else
                    activeCounts[job.Queue] = 1;
            }
        }

        return new ValueTask<IEnumerable<JobHotEntity>>(lockedJobs);
    }

    public ValueTask<bool> MarkAsProcessingAsync(
        string jobId,
        CancellationToken ct = default,
        string? workerId = null)
    {
        if (_hotJobs.TryGetValue(jobId, out var job))
        {
            if (workerId is not null &&
                (job.Status != JobStatus.Fetched || !string.Equals(job.WorkerId, workerId, StringComparison.Ordinal)))
            {
                // A worker-owned transition must prove both identity and state. This prevents an
                // old prefetched copy from becoming Processing after ReleaseJobAsync or abandoned
                // fetch recovery already returned the persisted row to Pending.
                return new ValueTask<bool>(false);
            }

            var now = DateTime.UtcNow;
            var processing = job with
            {
                Status = JobStatus.Processing,
                // Count attempts at the execution boundary, not at fetch time. Fetching only
                // reserves work in memory; it should not burn retry budget if the job is later
                // released because of pause, shutdown, or abandoned-fetch recovery.
                AttemptCount = job.AttemptCount + 1,
                StartedAtUtc = now,
                HeartbeatUtc = now,
                LastUpdatedUtc = now
            };
            return new ValueTask<bool>(_hotJobs.TryUpdate(jobId, processing, job));
        }
        return new ValueTask<bool>(false);
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

    public ValueTask<bool> ArchiveSucceededAsync(
        string jobId,
        double? durationMs = null,
        CancellationToken ct = default,
        string? workerId = null)
    {
        lock (_transitionLock)
        {
            if (!_hotJobs.TryGetValue(jobId, out var job) || !IsOwnedBy(job, workerId))
                return new ValueTask<bool>(false);

            if (!_hotJobs.TryRemove(jobId, out job))
                return new ValueTask<bool>(false);

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
            RecordMetricBucket(job.Queue, failed: false, failureReason: null, durationMs: archived.DurationMs, observedAtUtc: now);
        }
        return new ValueTask<bool>(true);
    }

    public ValueTask<bool> ArchiveFailedAsync(
        string jobId,
        string errorDetails,
        CancellationToken ct = default,
        string? workerId = null,
        FailureReason failureReason = FailureReason.MaxRetriesExceeded)
    {
        // The DLQ reason is an operator-facing taxonomy, not just an implementation detail.
        // Keeping it explicit lets dashboards distinguish poison pills, timeouts, throttling,
        // and normal transient exhaustion without scraping free-form exception text.
        return MoveToDLQ(jobId, failureReason, errorDetails, workerId);
    }

    public ValueTask<bool> ArchiveCancelledAsync(
        string jobId,
        string? cancelledBy = null,
        CancellationToken ct = default,
        string? workerId = null)
    {
        var error = cancelledBy != null
            ? $"Cancelled by admin: {cancelledBy}"
            : "Cancelled by admin";
        return MoveToDLQ(jobId, FailureReason.Cancelled, error, workerId);
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
                    MoveToDLQ(id, FailureReason.Cancelled, error, workerId: null).GetAwaiter().GetResult();
                    count++;
                }
            }
        }
        return new ValueTask<int>(count);
    }

    public ValueTask<bool> ArchiveZombieAsync(
        string jobId,
        CancellationToken ct = default,
        string? workerId = null)
    {
        return MoveToDLQ(jobId, FailureReason.Zombie, "Zombie: Worker heartbeat expired", workerId);
    }

    public ValueTask ResurrectAsync(
        string jobId,
        JobDataUpdateDto? updates = null,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        _ = RepairAndRequeueDLQCore(jobId, updates, resurrectedBy);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RepairAndRequeueDLQAsync(
        string jobId,
        JobDataUpdateDto updates,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        // Repair-and-requeue is the operator-safe version of resurrection. It uses the same
        // atomic DLQ -> Hot move as ordinary retry, but the return value lets the UI distinguish
        // a successful repair from a row that another admin already purged or requeued.
        return new ValueTask<bool>(RepairAndRequeueDLQCore(jobId, updates, resurrectedBy));
    }

    private bool RepairAndRequeueDLQCore(
        string jobId,
        JobDataUpdateDto? updates,
        string? resurrectedBy)
    {
        lock (_transitionLock)
        {
            if (!_dlqJobs.TryRemove(jobId, out var dlqJob))
                return false;

            var now = DateTime.UtcNow;
            var resurrected = new JobHotEntity(
                Id: dlqJob.Id,
                Queue: dlqJob.Queue,
                Type: dlqJob.Type,
                Payload: updates?.Payload ?? dlqJob.Payload,
                Tags: updates?.Tags ?? dlqJob.Tags,
                IdempotencyKey: null,
                Priority: updates?.Priority ?? 10,
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
        }

        return true;
    }

    public ValueTask<int> ResurrectBatchAsync(string[] jobIds, string? resurrectedBy = null, CancellationToken ct = default)
    {
        int count = 0;
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
        if (_hotJobs.TryGetValue(jobId, out var job))
        {
            var released = job with
            {
                Status = JobStatus.Pending,
                WorkerId = null,
                LastUpdatedUtc = DateTime.UtcNow
            };
            _hotJobs.TryUpdate(jobId, released, job);
        }
        return ValueTask.CompletedTask;
    }

    // ========================================================================
    // RETRY LOGIC (Stays in Hot)
    // ========================================================================

    public ValueTask<bool> RescheduleForRetryAsync(
        string jobId,
        DateTime scheduledAtUtc,
        int newAttemptCount,
        string lastError,
        CancellationToken ct = default,
        string? workerId = null)
    {
        if (_hotJobs.TryGetValue(jobId, out var job))
        {
            if (!IsOwnedBy(job, workerId))
                return new ValueTask<bool>(false);

            var rescheduled = job with
            {
                Status = JobStatus.Pending,
                ScheduledAtUtc = scheduledAtUtc,
                AttemptCount = newAttemptCount,
                WorkerId = null,
                HeartbeatUtc = null,
                LastUpdatedUtc = DateTime.UtcNow
            };
            if (_hotJobs.TryUpdate(jobId, rescheduled, job))
            {
                IncrementStats(job.Queue, retried: 1);
                return new ValueTask<bool>(true);
            }
        }
        return new ValueTask<bool>(false);
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
            if (_dlqJobs.TryRemove(id, out var removed))
            {
                // Purge is not just a dictionary delete: failed counters are operator-facing
                // truth. Keeping the in-memory backend aligned with SQL makes demos and tests
                // teach the same accounting rule that production storage follows.
                DecrementStats(removed.Queue, failed: 1);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<DlqBulkOperationPreviewDto> PreviewDLQBulkOperationAsync(
        DlqBulkOperationFilterDto filter,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var limit = NormalizeDlqBulkLimit(filter);
        var matches = OrderDlqBulkCandidates(ApplyDlqBulkFilter(_dlqJobs.Values, filter)).ToList();
        var sampleIds = matches.Take(10).Select(j => j.Id).ToArray();

        return new ValueTask<DlqBulkOperationPreviewDto>(
            new DlqBulkOperationPreviewDto(matches.Count, limit, sampleIds));
    }

    public ValueTask<int> PurgeDLQByFilterAsync(
        DlqBulkOperationFilterDto filter,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var limit = NormalizeDlqBulkLimit(filter);
        var purged = 0;

        lock (_transitionLock)
        {
            var candidates = OrderDlqBulkCandidates(ApplyDlqBulkFilter(_dlqJobs.Values, filter))
                .Take(limit)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (_dlqJobs.TryRemove(candidate.Id, out var removed))
                {
                    DecrementStats(removed.Queue, failed: 1);
                    purged++;
                }
            }
        }

        return new ValueTask<int>(purged);
    }

    public ValueTask<int> ResurrectDLQByFilterAsync(
        DlqBulkOperationFilterDto filter,
        string? resurrectedBy = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var limit = NormalizeDlqBulkLimit(filter);
        var resurrectedCount = 0;

        lock (_transitionLock)
        {
            var candidates = OrderDlqBulkCandidates(ApplyDlqBulkFilter(_dlqJobs.Values, filter))
                .Take(limit)
                .ToList();

            foreach (var dlqJob in candidates)
            {
                if (!_dlqJobs.TryRemove(dlqJob.Id, out var removed))
                    continue;

                var now = DateTime.UtcNow;
                var resurrected = new JobHotEntity(
                    Id: removed.Id,
                    Queue: removed.Queue,
                    Type: removed.Type,
                    Payload: removed.Payload,
                    Tags: removed.Tags,
                    IdempotencyKey: null,
                    Priority: 10,
                    Status: JobStatus.Pending,
                    AttemptCount: 0,
                    WorkerId: null,
                    HeartbeatUtc: null,
                    ScheduledAtUtc: null,
                    CreatedAtUtc: removed.CreatedAtUtc,
                    StartedAtUtc: null,
                    LastUpdatedUtc: now,
                    CreatedBy: removed.CreatedBy,
                    LastModifiedBy: resurrectedBy
                );

                _hotJobs[removed.Id] = resurrected;
                DecrementStats(removed.Queue, failed: 1);
                resurrectedCount++;
            }
        }

        return new ValueTask<int>(resurrectedCount);
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
        var hotValues = _hotJobs.Values;
        var statsValues = _stats.Values;

        var stats = new StatsSummaryEntity(
            Queue: null,
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

    public ValueTask<SystemHealthDto> GetSystemHealthAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var oneMinuteCutoff = now.AddMinutes(-1);
        var fiveMinuteCutoff = now.AddMinutes(-5);

        var allQueues = _queues.Keys
            .Concat(_hotJobs.Values.Select(j => j.Queue))
            .Concat(_archiveJobs.Values.Select(j => j.Queue))
            .Concat(_dlqJobs.Values.Select(j => j.Queue))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(q => q, StringComparer.Ordinal)
            .ToList();

        var pendingLagByQueue = _hotJobs.Values
            .Where(j => j.Status == JobStatus.Pending)
            .Where(j => !j.ScheduledAtUtc.HasValue || j.ScheduledAtUtc <= now)
            .GroupBy(j => j.Queue, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    // Scheduled jobs should not create false saturation before they are eligible.
                    // Once they become eligible, lag is measured from ScheduledAtUtc so delayed
                    // workloads do not look unhealthy simply because they were intentionally delayed.
                    var lags = g.Select(j => Math.Max(0, (now - (j.ScheduledAtUtc ?? j.CreatedAtUtc)).TotalSeconds)).ToList();
                    return new QueueHealthDto(
                        g.Key,
                        lags.Count,
                        lags.Count == 0 ? 0 : lags.Average(),
                        lags.Count == 0 ? 0 : lags.Max());
                },
                StringComparer.Ordinal);

        var queueHealth = allQueues
            .Select(queue => pendingLagByQueue.GetValueOrDefault(queue) ?? new QueueHealthDto(queue, 0, 0, 0))
            .ToList();

        var metricSnapshots = _metricBuckets
            .Where(kvp => kvp.Key.BucketStartUtc >= fiveMinuteCutoff)
            .Select(kvp => kvp.Value.Snapshot(kvp.Key))
            .ToList();

        var processedLastMinute = metricSnapshots
            .Where(b => b.Key.BucketStartUtc >= oneMinuteCutoff)
            .Sum(b => b.CompletedCount);
        var bucketFailedLastMinute = metricSnapshots
            .Where(b => b.Key.BucketStartUtc >= oneMinuteCutoff && b.Key.Outcome == MetricOutcomeFailed)
            .Sum(b => b.CompletedCount);
        var processedLastFiveMinutes = metricSnapshots.Sum(b => b.CompletedCount);
        var bucketFailedLastFiveMinutes = metricSnapshots
            .Where(b => b.Key.Outcome == MetricOutcomeFailed)
            .Sum(b => b.CompletedCount);

        var topErrors = _dlqJobs.Values
            .GroupBy(j => new { j.FailureReason, Prefix = NormalizeErrorPrefix(j.ErrorDetails) })
            .Select(g => new DlqErrorGroupDto(
                g.Key.FailureReason,
                g.Key.Prefix,
                g.LongCount(),
                g.Max(j => j.FailedAtUtc)))
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.LatestFailedAtUtc)
            .Take(5)
            .ToList();

        var health = new SystemHealthDto(
            GeneratedAtUtc: now,
            Queues: queueHealth,
            JobsPerSecondLastMinute: CalculateJobsPerSecond(processedLastMinute, 60),
            JobsPerSecondLastFiveMinutes: CalculateJobsPerSecond(processedLastFiveMinutes, 300),
            FailureRateLastMinutePercent: CalculateFailureRatePercent(bucketFailedLastMinute, processedLastMinute),
            FailureRateLastFiveMinutesPercent: CalculateFailureRatePercent(bucketFailedLastFiveMinutes, processedLastFiveMinutes),
            TopErrors: topErrors);

        return new ValueTask<SystemHealthDto>(health);
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
                MaxWorkers: queueData.MaxWorkers,
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

    public ValueTask SetQueueMaxWorkersAsync(string queueName, int? maxWorkers, CancellationToken ct = default)
    {
        _queues.AddOrUpdate(queueName,
            new QueueData(queueName) { MaxWorkers = maxWorkers },
            (_, old) => { old.MaxWorkers = maxWorkers; old.LastUpdatedUtc = DateTime.UtcNow; return old; });
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

        // Fetched recovery uses its own timeout domain. A buffered job has not executed user
        // code yet, so returning it to Pending is safe; the separate timeout prevents long
        // prefetch waits from being judged by the stricter Processing heartbeat policy.
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

        var processingJobs = _hotJobs.Values
            .Where(j => j.Status == JobStatus.Processing)
            .ToList();

        foreach (var job in processingJobs)
        {
            int timeout = globalTimeoutSeconds;
            if (_queues.TryGetValue(job.Queue, out var q) && q.ZombieTimeoutSeconds.HasValue)
                timeout = q.ZombieTimeoutSeconds.Value;

            var heartbeat = job.HeartbeatUtc ?? job.StartedAtUtc ?? job.LastUpdatedUtc;
            if (heartbeat < now.AddSeconds(-timeout))
            {
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
                        RecordMetricBucket(removed.Queue, failed: true, FailureReason.Zombie, durationMs: null, observedAtUtc: now);
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

        if (!string.IsNullOrEmpty(filter.Queue))
            query = query.Where(x => x.Queue == filter.Queue);

        if (filter.FromUtc.HasValue)
            query = query.Where(x => x.FinishedAtUtc >= filter.FromUtc);

        if (filter.ToUtc.HasValue)
            query = query.Where(x => x.FinishedAtUtc <= filter.ToUtc);

        if (!string.IsNullOrEmpty(filter.SearchTerm))
            query = query.Where(x => x.Id.Contains(filter.SearchTerm, StringComparison.OrdinalIgnoreCase) ||
                                     x.Type.Contains(filter.SearchTerm, StringComparison.OrdinalIgnoreCase));

        if (filter.SortDescending)
            query = query.OrderByDescending(x => x.FinishedAtUtc);
        else
            query = query.OrderBy(x => x.FinishedAtUtc);

        var total = query.Count();

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

        if (!string.IsNullOrEmpty(filter.Queue))
            query = query.Where(x => x.Queue == filter.Queue);

        if (filter.FailureReason.HasValue)
        {
            // DLQ reason filtering is a taxonomy filter, not a text search. Keeping it typed
            // lets operators separate throttling, fatal poison pills, timeouts, and ordinary
            // transient exhaustion even when error messages are noisy or localized.
            query = query.Where(x => x.FailureReason == filter.FailureReason.Value);
        }

        if (filter.FromUtc.HasValue)
            query = query.Where(x => x.CreatedAtUtc >= filter.FromUtc);

        if (filter.ToUtc.HasValue)
            query = query.Where(x => x.CreatedAtUtc <= filter.ToUtc);

        if (!string.IsNullOrEmpty(filter.SearchTerm))
        {
            query = query.Where(x => MatchesDlqSearch(x, filter.SearchTerm));
        }

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

    private static string? NormalizeIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return null;

        var normalized = idempotencyKey.Trim();
        if (normalized.Length > 255)
            throw new ArgumentException("IdempotencyKey exceeds maximum length of 255 characters.", nameof(idempotencyKey));

        return normalized;
    }

    private static int NormalizeDlqBulkLimit(DlqBulkOperationFilterDto filter)
    {
        var requested = filter.MaxJobs <= 0
            ? DlqBulkOperationFilterDto.DefaultMaxJobs
            : filter.MaxJobs;

        return Math.Clamp(requested, 1, DlqBulkOperationFilterDto.AbsoluteMaxJobs);
    }

    private static IEnumerable<JobDLQEntity> ApplyDlqBulkFilter(
        IEnumerable<JobDLQEntity> source,
        DlqBulkOperationFilterDto filter)
    {
        var query = source;

        if (!string.IsNullOrWhiteSpace(filter.Queue))
            query = query.Where(j => string.Equals(j.Queue, filter.Queue.Trim(), StringComparison.OrdinalIgnoreCase));

        if (filter.FailureReason.HasValue)
            query = query.Where(j => j.FailureReason == filter.FailureReason.Value);

        if (!string.IsNullOrWhiteSpace(filter.Type))
            query = query.Where(j => string.Equals(j.Type, filter.Type.Trim(), StringComparison.OrdinalIgnoreCase));

        if (filter.FromUtc.HasValue)
            query = query.Where(j => j.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(j => j.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
            query = query.Where(j => MatchesDlqSearch(j, filter.SearchTerm));

        return query;
    }

    private static IOrderedEnumerable<JobDLQEntity> OrderDlqBulkCandidates(IEnumerable<JobDLQEntity> source)
    {
        // Filtered bulk actions use the same stable order for preview and execution. Operators see
        // the newest failures first, while Id acts as a deterministic tie-breaker for repeatability.
        return source
            .OrderByDescending(j => j.FailedAtUtc)
            .ThenBy(j => j.Id, StringComparer.Ordinal);
    }

    private static bool MatchesDlqSearch(JobDLQEntity job, string searchTerm)
    {
        return Contains(job.Id, searchTerm)
            || Contains(job.Type, searchTerm)
            || Contains(job.Tags, searchTerm)
            || Contains(job.ErrorDetails, searchTerm);
    }

    private static bool Contains(string? value, string searchTerm) =>
        !string.IsNullOrEmpty(value)
        && value.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);

    private void RecordMetricBucket(
        string queue,
        bool failed,
        FailureReason? failureReason,
        double? durationMs,
        DateTime observedAtUtc)
    {
        // Metric buckets are intentionally event-like: they remember that a completion happened
        // even if the operator later requeues, purges, or repairs the DLQ row. The older/simple
        // dashboard approach counted Archive/DLQ rows directly, but that let administrative cleanup
        // rewrite recent failure-rate history. Buckets are the more production-grade boundary.
        var key = new MetricBucketKey(
            TruncateToSecond(observedAtUtc),
            queue,
            failed ? MetricOutcomeFailed : MetricOutcomeSucceeded,
            failed ? (int)(failureReason ?? FailureReason.MaxRetriesExceeded) : NoFailureReason);

        var bucket = _metricBuckets.GetOrAdd(key, _ => new MetricBucketData());
        bucket.Add(durationMs);
    }

    private ValueTask<bool> MoveToDLQ(
        string jobId,
        FailureReason reason,
        string errorDetails,
        string? workerId)
    {
        lock (_transitionLock)
        {
            if (!_hotJobs.TryGetValue(jobId, out var job) || !IsOwnedBy(job, workerId))
                return new ValueTask<bool>(false);

            if (!_hotJobs.TryRemove(jobId, out job))
                return new ValueTask<bool>(false);

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
            RecordMetricBucket(job.Queue, failed: true, reason, durationMs: null, observedAtUtc: now);
        }
        return new ValueTask<bool>(true);
    }

    private static bool IsOwnedBy(JobHotEntity job, string? workerId)
    {
        // Null workerId is reserved for administrative or recovery operations that
        // intentionally bypass worker ownership. Normal workers must prove that the
        // row is still assigned to them before they can finalize or reschedule it.
        return workerId is null || string.Equals(job.WorkerId, workerId, StringComparison.Ordinal);
    }

    private static double CalculateJobsPerSecond(long processed, int windowSeconds) =>
        windowSeconds <= 0 ? 0 : processed / (double)windowSeconds;

    private static double CalculateFailureRatePercent(long failed, long processed) =>
        processed == 0 ? 0 : (failed * 100d) / processed;

    private static DateTime TruncateToSecond(DateTime utc)
    {
        var normalized = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return new DateTime(
            normalized.Ticks - (normalized.Ticks % TimeSpan.TicksPerSecond),
            DateTimeKind.Utc);
    }

    private static string NormalizeErrorPrefix(string? errorDetails)
    {
        if (string.IsNullOrWhiteSpace(errorDetails))
            return "(empty error details)";

        var firstLine = errorDetails
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        while (firstLine.Contains("  ", StringComparison.Ordinal))
        {
            firstLine = firstLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        return firstLine.Length <= ErrorPrefixLength
            ? firstLine
            : firstLine[..ErrorPrefixLength];
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

    private bool IsQueueLimitReached(string queue, Dictionary<string, int> activeCounts)
    {
        if (_queues.TryGetValue(queue, out var q) && q.MaxWorkers.HasValue)
        {
            if (activeCounts.TryGetValue(queue, out var active))
            {
                return active >= q.MaxWorkers.Value;
            }
        }
        return false;
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

        // In-memory mode uses a retention cap, not a hard producer admission gate. Hot jobs are
        // accepted work and must remain visible to the worker, so pressure is relieved from the
        // historical pillars first. SQL mode is the correct choice when operators need a durable,
        // database-sized backlog instead of process memory acting as the pressure boundary.
        var archiveToRemove = _archiveJobs.Values
            .OrderBy(j => j.FinishedAtUtc)
            .Take(1000)
            .Select(j => j.Id)
            .ToList();

        foreach (var id in archiveToRemove)
            _archiveJobs.TryRemove(id, out _);

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
        public int? MaxWorkers { get; set; }
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

    private readonly record struct MetricBucketKey(
        DateTime BucketStartUtc,
        string Queue,
        int Outcome,
        int FailureReason);

    private sealed class MetricBucketData
    {
        private readonly object _lock = new();
        private long _completedCount;
        private long _durationCount;
        private double _totalDurationMs;
        private double? _maxDurationMs;

        public void Add(double? durationMs)
        {
            lock (_lock)
            {
                _completedCount++;

                if (!durationMs.HasValue)
                    return;

                _durationCount++;
                _totalDurationMs += durationMs.Value;
                _maxDurationMs = !_maxDurationMs.HasValue || durationMs.Value > _maxDurationMs.Value
                    ? durationMs.Value
                    : _maxDurationMs;
            }
        }

        public MetricBucketSnapshot Snapshot(MetricBucketKey key)
        {
            lock (_lock)
            {
                return new MetricBucketSnapshot(
                    key,
                    _completedCount,
                    _durationCount,
                    _totalDurationMs,
                    _maxDurationMs);
            }
        }
    }

    private readonly record struct MetricBucketSnapshot(
        MetricBucketKey Key,
        long CompletedCount,
        long DurationCount,
        double TotalDurationMs,
        double? MaxDurationMs);
}
