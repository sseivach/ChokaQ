using ChokaQ.Abstractions;
using ChokaQ.SampleApp.Jobs;

namespace ChokaQ.SampleApp.Handlers;

// The logic is also fully controlled by the Consumer App.
public class PrintMessageJobHandler : IChokaQJobHandler<PrintMessageJob>
{
    private readonly ILogger<PrintMessageJobHandler> _logger;

    public PrintMessageJobHandler(ILogger<PrintMessageJobHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(PrintMessageJob job, CancellationToken ct)
    {
        _logger.LogInformation($"[APP HANDLER] Processing: {job.Text}");

        // Simulating some heavy work defined by the app
        await Task.Delay(1000, ct);

        _logger.LogInformation($"[APP HANDLER] Completed: {job.Text}");
    }
}