using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.NuGetLab.Jobs.Handlers;

public sealed class ReportRenderHandler : IChokaQJobHandler<ReportRenderJob>
{
    private readonly IJobContext _context;
    private readonly ILogger<ReportRenderHandler> _logger;

    public ReportRenderHandler(IJobContext context, ILogger<ReportRenderHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task HandleAsync(ReportRenderJob job, CancellationToken ct)
    {
        for (var progress = 20; progress <= 90; progress += 20)
        {
            await Task.Delay(450, ct);
            await _context.ReportProgressAsync(progress);
        }

        _logger.LogInformation("Rendered report for {Tenant} at {BusinessDate}", job.Tenant, job.BusinessDate);
    }
}
