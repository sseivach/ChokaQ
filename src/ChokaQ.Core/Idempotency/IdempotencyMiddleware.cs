using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Middleware;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChokaQ.Core.Idempotency;

/// <summary>
/// Middleware that short-circuits job execution if a cached result exists for the job's idempotency key.
///
/// [ARCHITECTURE PATTERN - "Level 2 Idempotency"]:
/// This middleware sits at the FRONT of the execution pipeline:
///
///   [IdempotencyMiddleware] → [LoggingMiddleware] → [Handler]
///
/// Execution flow:
///   1. Check IIdempotencyStore for a cached result using the job's idempotency key.
///   2. CACHE HIT:  Return the cached result immediately. Handler is NEVER invoked.
///   3. CACHE MISS: Invoke the rest of the pipeline (handler executes).
///                  Store the result in IIdempotencyStore for future duplicate requests.
///
/// Design decisions:
///   - This is an opt-in plugin. Core pipeline has zero knowledge of idempotency.
///   - The job itself provides its key via IIdempotentJob (user-defined contract).
///   - Only jobs implementing IIdempotentJob are intercepted; others pass through unchanged.
/// </summary>
public sealed class IdempotencyMiddleware : IChokaQMiddleware
{
    private readonly IIdempotencyStore _store;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(IIdempotencyStore store, ILogger<IdempotencyMiddleware> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task InvokeAsync(IJobContext context, object? job, JobDelegate next)
    {
        // Only intercept jobs that explicitly opt-in to result idempotency
        if (job is not IIdempotentJob idempotentJob)
        {
            await next();
            return;
        }

        var key = idempotentJob.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("[Idempotency] Job {JobId} implements IIdempotentJob but IdempotencyKey is null. Skipping cache.", context.JobId);
            await next();
            return;
        }

        // ── CACHE CHECK ──────────────────────────────────────────────────────────
        var cached = await _store.TryGetResultAsync(key, CancellationToken.None);
        if (cached != null)
        {
            _logger.LogInformation(
                "[Idempotency] CACHE HIT for key '{Key}'. Job {JobId} skipped — returning stored result.",
                key, context.JobId);
            return; // Short-circuit: handler is never invoked
        }

        // ── CACHE MISS: Execute handler, then store result ───────────────────────
        await next();

        // Store a minimal "completed" marker so future duplicates are short-circuited.
        // If the job produces a meaningful return value, a custom IIdempotencyStore
        // implementation can capture and serialize it here.
        var resultPayload = JsonSerializer.Serialize(new { CompletedAt = DateTimeOffset.UtcNow, JobId = context.JobId });
        await _store.StoreResultAsync(key, resultPayload, idempotentJob.ResultTtl, CancellationToken.None);

        _logger.LogDebug("[Idempotency] Result stored for key '{Key}' (TTL: {Ttl}).", key, idempotentJob.ResultTtl);
    }
}
