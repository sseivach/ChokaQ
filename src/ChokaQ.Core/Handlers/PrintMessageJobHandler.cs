using ChokaQ.Abstractions;
using ChokaQ.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Handlers;

public class PrintMessageJobHandler : IChokaQJobHandler<PrintMessageJob>
{
    private readonly ILogger<PrintMessageJobHandler> _logger;

    public PrintMessageJobHandler(ILogger<PrintMessageJobHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(PrintMessageJob job, CancellationToken ct)
    {
        _logger.LogWarning($"[>>> HANDLER <<<] Starting to process: {job.Text}");

        await Task.Delay(2000, ct);

        _logger.LogWarning($"[>>> HANDLER <<<] Finished processing: {job.Text}");
    }
}