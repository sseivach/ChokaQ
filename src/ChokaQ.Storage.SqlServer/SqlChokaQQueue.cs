using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Observability;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Durable queue producer for SQL Server mode.
/// </summary>
public sealed class SqlChokaQQueue : IChokaQQueue
{
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly JobTypeRegistry _registry;
    private readonly ILogger<SqlChokaQQueue> _logger;

    public SqlChokaQQueue(
        IJobStorage storage,
        IChokaQNotifier notifier,
        JobTypeRegistry registry,
        ILogger<SqlChokaQQueue> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnqueueAsync<TJob>(
        TJob job,
        int priority = 10,
        string queue = "default",
        string? createdBy = null,
        string? tags = null,
        CancellationToken ct = default,
        TimeSpan? delay = null,
        string? idempotencyKey = null) where TJob : IChokaQJob
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));

        ValidateEnvelope(queue, createdBy, tags);
        var resolvedIdempotencyKey = ResolveIdempotencyKey(job, idempotencyKey);

        var jobTypeName = _registry.GetKeyByType(job.GetType()) ?? job.GetType().Name;
        if (jobTypeName.Length > 255)
            throw new InvalidOperationException($"Job Type Key '{jobTypeName}' exceeds maximum length of 255 characters.");

        var payload = JsonSerializer.Serialize(job, job.GetType());

        // SQL mode has a different producer/consumer boundary than in-memory mode:
        // the producer commits the job to durable storage and then stops. It must not
        // also push into an in-process Channel, because the SQL worker is the consumer
        // and polls the database. Mixing both paths creates a hidden memory buffer,
        // broken backpressure, and possible double-processing if both workers run.
        var enqueuedId = await _storage.EnqueueAsync(
            id: job.Id,
            queue: queue,
            jobType: jobTypeName,
            payload: payload,
            priority: priority,
            createdBy: createdBy,
            tags: tags,
            delay: delay,
            idempotencyKey: resolvedIdempotencyKey,
            ct: ct);

        if (!string.Equals(enqueuedId, job.Id, StringComparison.Ordinal))
        {
            // SQL storage returns the existing Hot job ID when the idempotency key already has
            // active work. No durable row was created, so emitting a "new job" notification would
            // teach operators the wrong mental model and make counters look like they advanced.
            _logger.LogDebug(
                ChokaQLogEvents.EnqueueDuplicateSkipped,
                "Skipped duplicate SQL enqueue for idempotency key {IdempotencyKey}. Existing job: {JobId}",
                resolvedIdempotencyKey,
                enqueuedId);
            return;
        }

        // The storage layer owns the durable write; the queue owns the user-facing
        // enqueue event. Notification failures must never roll back a committed job.
        // Operators can recover from a stale dashboard refresh, but not from a lost job.
        try
        {
            await _notifier.NotifyJobUpdatedAsync(new JobUpdateDto(
                JobId: enqueuedId,
                Type: jobTypeName,
                Queue: queue,
                Status: JobStatus.Pending,
                AttemptCount: 0,
                Priority: priority,
                DurationMs: null,
                CreatedBy: createdBy,
                StartedAtUtc: null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ChokaQLogEvents.EnqueueNotificationFailed,
                "Failed to notify UI about durable SQL enqueue for job {JobId}: {Message}",
                enqueuedId,
                ex.Message);
        }
    }

    private static void ValidateEnvelope(string queue, string? createdBy, string? tags)
    {
        if (string.IsNullOrWhiteSpace(queue))
            throw new ArgumentException("Queue name cannot be null or empty.", nameof(queue));

        if (queue.Length > 255)
            throw new ArgumentException($"Queue name '{queue}' exceeds maximum length of 255 characters.", nameof(queue));

        if (createdBy != null && createdBy.Length > 100)
            throw new ArgumentException($"CreatedBy '{createdBy}' exceeds maximum length of 100 characters.", nameof(createdBy));

        if (tags != null && tags.Length > 1000)
            throw new ArgumentException("Tags exceed maximum length of 1000 characters.", nameof(tags));
    }

    private static string? ResolveIdempotencyKey<TJob>(TJob job, string? explicitKey)
        where TJob : IChokaQJob
    {
        var key = !string.IsNullOrWhiteSpace(explicitKey)
            ? explicitKey
            : (job as IIdempotentJob)?.IdempotencyKey;

        if (string.IsNullOrWhiteSpace(key))
            return null;

        key = key.Trim();
        if (key.Length > 255)
        {
            throw new ArgumentException("IdempotencyKey exceeds maximum length of 255 characters.", nameof(explicitKey));
        }

        return key;
    }
}
