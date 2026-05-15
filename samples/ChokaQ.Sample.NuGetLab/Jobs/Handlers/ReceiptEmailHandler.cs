using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.NuGetLab.Jobs.Handlers;

public sealed class ReceiptEmailHandler : IChokaQJobHandler<ReceiptEmailJob>
{
    private readonly IJobContext _context;
    private readonly ILogger<ReceiptEmailHandler> _logger;

    public ReceiptEmailHandler(IJobContext context, ILogger<ReceiptEmailHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task HandleAsync(ReceiptEmailJob job, CancellationToken ct)
    {
        await _context.ReportProgressAsync(30);
        await Task.Delay(250, ct);
        await _context.ReportProgressAsync(70);
        await Task.Delay(250, ct);
        _logger.LogInformation("Receipt {ReceiptNumber} sent to {To}", job.ReceiptNumber, job.To);
    }
}
