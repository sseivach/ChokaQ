using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Middleware;
using ChokaQ.Abstractions.Observability;
using ChokaQ.Abstractions.Serialization;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Idempotency;

/// <summary>
/// Middleware that atomically claims an idempotency key before running a handler.
/// </summary>
internal sealed class IdempotencyMiddleware : IChokaQMiddleware
{
    private readonly IIdempotencyClaimStore _store;
    private readonly IChokaQJobSerializer _serializer;
    private readonly ChokaQIdempotencyOptions _options;
    private readonly ILogger<IdempotencyMiddleware> _logger;
    private readonly IChokaQMetrics? _metrics;

    public IdempotencyMiddleware(
        IIdempotencyClaimStore store,
        IChokaQJobSerializer serializer,
        ChokaQOptions options,
        ILogger<IdempotencyMiddleware> logger,
        IChokaQMetrics? metrics = null)
    {
        _store = store;
        _serializer = serializer;
        _options = options.Idempotency;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task InvokeAsync(IJobContext context, object? job, JobDelegate next)
    {
        if (job is not IIdempotentJob idempotentJob)
        {
            await next();
            return;
        }

        var key = idempotentJob.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _metrics?.RecordIdempotencyOutcome("empty_key");
            _logger.LogWarning(
                "[Idempotency] Job {JobId} implements IIdempotentJob but IdempotencyKey is null. Skipping claim.",
                context.JobId);
            await next();
            return;
        }

        var begin = await _store.TryBeginAsync(
            key,
            context.JobId,
            _options.InProgressTtl,
            CancellationToken.None);

        if (begin.Status == IdempotencyBeginStatus.AlreadyCompleted)
        {
            _metrics?.RecordIdempotencyOutcome("completed_duplicate");
            _logger.LogInformation(
                "[Idempotency] Completed marker hit for key '{Key}'. Job {JobId} skipped.",
                key,
                context.JobId);
            return;
        }

        if (begin.Status == IdempotencyBeginStatus.AlreadyInProgress)
        {
            _metrics?.RecordIdempotencyOutcome("in_progress_duplicate");
            _logger.LogInformation(
                "[Idempotency] In-progress claim hit for key '{Key}'. Job {JobId} skipped.",
                key,
                context.JobId);
            return;
        }

        _metrics?.RecordIdempotencyOutcome("claimed");

        try
        {
            await next();
        }
        catch
        {
            await _store.ReleaseAsync(key, context.JobId, CancellationToken.None);
            _metrics?.RecordIdempotencyOutcome("released");
            _logger.LogDebug(
                "[Idempotency] Released in-progress claim for key '{Key}' after handler failure.",
                key);
            throw;
        }

        var completionPayload = _serializer.SerializeCompletionMarker(context.JobId, DateTimeOffset.UtcNow);
        var completed = await _store.CompleteAsync(
            key,
            context.JobId,
            completionPayload,
            ResolveResultTtl(idempotentJob),
            CancellationToken.None);

        if (!completed)
        {
            _metrics?.RecordIdempotencyOutcome("complete_conflict");
            throw new InvalidOperationException(
                $"Idempotency completion failed for key '{key}' and job '{context.JobId}'.");
        }

        _metrics?.RecordIdempotencyOutcome("completed");
        _logger.LogDebug(
            "[Idempotency] Completed marker stored for key '{Key}' (TTL: {Ttl}).",
            key,
            ResolveResultTtl(idempotentJob));
    }

    private TimeSpan? ResolveResultTtl(IIdempotentJob job)
    {
        var ttl = job.ResultTtl ?? _options.DefaultResultTtl;

        if (ttl.HasValue)
        {
            if (_options.MinResultTtl.HasValue && ttl.Value < _options.MinResultTtl.Value)
            {
                throw new InvalidOperationException(
                    "Idempotency result TTL is smaller than Idempotency.MinResultTtl.");
            }

            if (_options.MaxResultTtl.HasValue && ttl.Value > _options.MaxResultTtl.Value)
            {
                throw new InvalidOperationException(
                    "Idempotency result TTL exceeds Idempotency.MaxResultTtl.");
            }
        }

        return ttl;
    }
}
