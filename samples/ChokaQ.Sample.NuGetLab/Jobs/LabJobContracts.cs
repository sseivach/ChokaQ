using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.NuGetLab.Jobs;

public sealed record ReceiptEmailJob(string To, string ReceiptNumber) : ChokaQBaseJob;

public sealed record PaymentCaptureJob(string OrderId, decimal Amount) : ChokaQBaseJob, IIdempotentJob
{
    public string IdempotencyKey => $"payment-capture:{OrderId}";
    public TimeSpan? ResultTtl => TimeSpan.FromMinutes(10);
}

public sealed record ReportRenderJob(string Tenant, DateOnly BusinessDate) : ChokaQBaseJob;

public sealed record WebhookDeliveryJob(string Endpoint, string Payload) : ChokaQBaseJob;

public sealed record PoisonPillJob(string Field, string Reason) : ChokaQBaseJob;

public sealed record ThrottledPartnerJob(string Partner, int SucceedAfterAttempts) : ChokaQBaseJob;

public sealed record SlowJob(int Seconds, string Label) : ChokaQBaseJob;
