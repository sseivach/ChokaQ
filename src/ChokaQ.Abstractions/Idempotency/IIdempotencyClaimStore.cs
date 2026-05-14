namespace ChokaQ.Abstractions.Idempotency;

public enum IdempotencyBeginStatus
{
    Claimed = 0,
    AlreadyCompleted = 1,
    AlreadyInProgress = 2
}

public sealed record IdempotencyBeginResult(
    IdempotencyBeginStatus Status,
    string? CompletedPayload = null)
{
    public static IdempotencyBeginResult Claimed { get; } = new(IdempotencyBeginStatus.Claimed);

    public static IdempotencyBeginResult AlreadyCompleted(string completedPayload) =>
        new(IdempotencyBeginStatus.AlreadyCompleted, completedPayload);

    public static IdempotencyBeginResult AlreadyInProgress { get; } =
        new(IdempotencyBeginStatus.AlreadyInProgress);
}

/// <summary>
/// Atomic idempotency claim store for preventing concurrent duplicate execution.
/// </summary>
public interface IIdempotencyClaimStore
{
    ValueTask<IdempotencyBeginResult> TryBeginAsync(
        string idempotencyKey,
        string jobId,
        TimeSpan inProgressTtl,
        CancellationToken ct = default);

    ValueTask<bool> CompleteAsync(
        string idempotencyKey,
        string jobId,
        string completionPayload,
        TimeSpan? completedTtl = null,
        CancellationToken ct = default);

    ValueTask<bool> ReleaseAsync(
        string idempotencyKey,
        string jobId,
        CancellationToken ct = default);
}

