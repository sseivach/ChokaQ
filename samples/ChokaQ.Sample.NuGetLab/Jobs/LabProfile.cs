using ChokaQ.Abstractions.Jobs;
using ChokaQ.Sample.NuGetLab.Jobs.Handlers;

namespace ChokaQ.Sample.NuGetLab.Jobs;

public sealed class LabProfile : ChokaQJobProfile
{
    public LabProfile()
    {
        CreateJob<ReceiptEmailJob, ReceiptEmailHandler>("lab.receipt-email.v1");
        CreateJob<PaymentCaptureJob, PaymentCaptureHandler>("lab.payment-capture.v1");
        CreateJob<ReportRenderJob, ReportRenderHandler>("lab.report-render.v1");
        CreateJob<WebhookDeliveryJob, WebhookDeliveryHandler>("lab.webhook-delivery.v1");
        CreateJob<PoisonPillJob, PoisonPillHandler>("lab.poison-pill.v1");
        CreateJob<ThrottledPartnerJob, ThrottledPartnerHandler>("lab.throttled-partner.v1");
        CreateJob<SlowJob, SlowJobHandler>("lab.slow-job.v1");
    }
}
