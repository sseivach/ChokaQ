namespace ChokaQ.Abstractions.Idempotency;

/// <summary>
/// Marker interface that opts a job into Result-Based Idempotency.
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
///     // Cache the result for 24 hours.
///     public TimeSpan? ResultTtl => TimeSpan.FromHours(24);
/// }
///
/// // 2. Enable the plugin in your DI setup (call ONCE):
/// services.AddChokaQ(options => options.AddProfile<PaymentProfile>())
///         .AddResultIdempotency();     // ← enables Level 2 for IIdempotentJob jobs
/// </code>
///
/// [DESIGN NOTE]:
/// This is an explicit opt-in, not magic. The developer consciously decides 
/// which jobs are critical enough to warrant result caching, avoiding the
/// storage overhead for the 80% of jobs that don't need it.
/// </summary>
public interface IIdempotentJob
{
    /// <summary>
    /// A deterministic, globally unique key for this specific logical operation.
    /// Examples: "payment:{orderId}", "invoice:{invoiceId}", "email-confirmation:{userId}:{date}".
    /// </summary>
    string IdempotencyKey { get; }

    /// <summary>
    /// How long the cached result should be retained.
    /// Null means "store indefinitely" (not recommended for high-volume systems).
    /// </summary>
    TimeSpan? ResultTtl { get; }
}
