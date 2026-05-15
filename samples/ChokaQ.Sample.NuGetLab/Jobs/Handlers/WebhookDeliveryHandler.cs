using System.Collections.Concurrent;
using ChokaQ.Abstractions.Jobs;

namespace ChokaQ.Sample.NuGetLab.Jobs.Handlers;

public sealed class WebhookDeliveryHandler : IChokaQJobHandler<WebhookDeliveryJob>
{
    private static readonly ConcurrentDictionary<string, int> Attempts = new();
    private readonly ILogger<WebhookDeliveryHandler> _logger;

    public WebhookDeliveryHandler(ILogger<WebhookDeliveryHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(WebhookDeliveryJob job, CancellationToken ct)
    {
        await Task.Delay(300, ct);
        var attempt = Attempts.AddOrUpdate(job.Id, 1, (_, current) => current + 1);

        if (job.Endpoint.StartsWith("retry://", StringComparison.OrdinalIgnoreCase) && attempt < 2)
        {
            throw new InvalidOperationException($"Transient partner outage on attempt {attempt}");
        }

        _logger.LogInformation("Delivered webhook to {Endpoint} on attempt {Attempt}", job.Endpoint, attempt);
    }
}
