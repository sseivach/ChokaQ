using System.Collections.Concurrent;
using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.NuGetLab.Jobs.Handlers;

public sealed class ThrottledPartnerHandler : IChokaQJobHandler<ThrottledPartnerJob>
{
    private static readonly ConcurrentDictionary<string, int> Attempts = new();
    private readonly ILogger<ThrottledPartnerHandler> _logger;

    public ThrottledPartnerHandler(ILogger<ThrottledPartnerHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(ThrottledPartnerJob job, CancellationToken ct)
    {
        await Task.Delay(250, ct);
        var attempt = Attempts.AddOrUpdate(job.Id, 1, (_, current) => current + 1);

        if (attempt < job.SucceedAfterAttempts)
        {
            throw new PartnerThrottledException(TimeSpan.FromSeconds(3), job.Partner, attempt);
        }

        _logger.LogInformation("Partner {Partner} accepted work on attempt {Attempt}", job.Partner, attempt);
    }
}
