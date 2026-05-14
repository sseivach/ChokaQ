using ChokaQ.Abstractions.Idempotency;

namespace ChokaQ.Core.Idempotency;

internal sealed class LegacyIdempotencyClaimStoreAdapter : IIdempotencyClaimStore
{
    private readonly IIdempotencyStore _store;

    public LegacyIdempotencyClaimStoreAdapter(IIdempotencyStore store)
    {
        _store = store;
    }

    public async ValueTask<IdempotencyBeginResult> TryBeginAsync(
        string idempotencyKey,
        string jobId,
        TimeSpan inProgressTtl,
        CancellationToken ct = default)
    {
        var completed = await _store.TryGetResultAsync(idempotencyKey, ct);
        return completed is not null
            ? IdempotencyBeginResult.AlreadyCompleted(completed)
            : IdempotencyBeginResult.Claimed;
    }

    public async ValueTask<bool> CompleteAsync(
        string idempotencyKey,
        string jobId,
        string completionPayload,
        TimeSpan? completedTtl = null,
        CancellationToken ct = default)
    {
        await _store.StoreResultAsync(idempotencyKey, completionPayload, completedTtl, ct);
        return true;
    }

    public ValueTask<bool> ReleaseAsync(
        string idempotencyKey,
        string jobId,
        CancellationToken ct = default) =>
        ValueTask.FromResult(true);
}

