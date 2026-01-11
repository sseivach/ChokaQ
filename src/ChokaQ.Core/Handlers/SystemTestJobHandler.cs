using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Jobs;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Handlers;

public class SystemTestJobHandler : IChokaQJobHandler<SystemTestJob>
{
    private readonly IJobContext _context;

    public SystemTestJobHandler(IJobContext context)
    {
        _context = context;
    }

    public async Task HandleAsync(SystemTestJob job, CancellationToken ct)
    {
        for (int i = 0; i <= 100; i += 20)
        {
            if (ct.IsCancellationRequested) break;
            await _context.ReportProgressAsync(i);
            await Task.Delay(job.DurationMs / 5, ct);
        }

        if (job.ShouldFail)
        {
            throw new Exception($"Simulated system failure for Job {job.Id}");
        }
    }
}