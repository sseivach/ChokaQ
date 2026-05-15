using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.NuGetLab.Jobs.Handlers;

public sealed class PaymentCaptureHandler : IChokaQJobHandler<PaymentCaptureJob>
{
    private readonly IJobContext _context;
    private readonly ILogger<PaymentCaptureHandler> _logger;

    public PaymentCaptureHandler(IJobContext context, ILogger<PaymentCaptureHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task HandleAsync(PaymentCaptureJob job, CancellationToken ct)
    {
        await _context.ReportProgressAsync(35);
        await Task.Delay(400, ct);
        await _context.ReportProgressAsync(80);
        await Task.Delay(200, ct);
        _logger.LogInformation("Captured {Amount} for {OrderId}", job.Amount, job.OrderId);
    }
}
