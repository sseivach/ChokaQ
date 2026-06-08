namespace ChokaQ.Abstractions.Idempotency;

/// <summary>
/// Marker interface that gives a job a deterministic idempotency key.
///
/// The same key participates in two different guarantees:
/// - Level 1 enqueue deduplication: ChokaQ prevents duplicate active Hot jobs with the same key.
/// - Level 2 claim idempotency: when the optional middleware is enabled, active duplicates can
///   be skipped and successful work leaves a completion marker.
///
/// [USAGE EXAMPLE]:
/// <code>
/// // 1. Add IIdempotentJob to your job class:
/// public class ProcessPaymentJob : IChokaQJob, IIdempotentJob
/// {
///     public string Id { get; set; } = "";
///     public string OrderId { get; set; } = "";
///
///     // Key must be deterministic and unique per logical operation.
///     // "payment:{OrderId}" means "this payment for this order".
///     public string IdempotencyKey => $"payment:{OrderId}";
///
///     // Keep the completion marker for 24 hours.
///     public TimeSpan? ResultTtl => TimeSpan.FromHours(24);
/// }
///
/// // 2. Enable the plugin in your DI setup (call ONCE):
/// services.AddChokaQ(options => options.AddProfile&lt;PaymentProfile&gt;())
///         .AddResultIdempotency();     // enables Level 2 for IIdempotentJob jobs
/// </code>
///
/// [DESIGN NOTE]:
/// This is an explicit opt-in, not magic. The developer consciously decides
/// which jobs have a stable business key. Enqueue dedupe is scoped to active
/// Hot jobs only; the idempotency middleware is the layer that can remember
/// completed markers after the job has left Hot.
/// </summary>
public interface IIdempotentJob
{
    /// <summary>
    /// A deterministic, globally unique key for this specific logical operation.
    /// Examples: "payment:{orderId}", "invoice:{invoiceId}", "email-confirmation:{userId}:{date}".
    /// </summary>
    string IdempotencyKey { get; }

    /// <summary>
    /// How long the completion marker should be retained.
    /// Null means "store indefinitely" (not recommended for high-volume systems).
    /// </summary>
    TimeSpan? ResultTtl { get; }
}
