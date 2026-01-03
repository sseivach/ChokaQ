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

    public Task HandleAsync(PrintMessageJob job, CancellationToken ct)
    {
        // Simulate some work
        _logger.LogWarning($"[>>> HANDLER <<<] Received message: {job.Text}");
        return Task.CompletedTask;
    }
}