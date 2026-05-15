using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.NuGetLab.Jobs.Handlers;

public sealed class SlowJobHandler : IChokaQJobHandler<SlowJob>
{
    private readonly IJobContext _context;
    private readonly ILogger<SlowJobHandler> _logger;

    public SlowJobHandler(IJobContext context, ILogger<SlowJobHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task HandleAsync(SlowJob job, CancellationToken ct)
    {
        var seconds = Math.Clamp(job.Seconds, 1, 60);
        for (var i = 1; i <= seconds; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await _context.ReportProgressAsync(Math.Min(95, i * 100 / seconds));
        }

        _logger.LogInformation("Finished slow job {Label} after {Seconds}s", job.Label, seconds);
    }
}
