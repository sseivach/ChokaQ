using ChokaQ.Abstractions;
using ChokaQ.SampleApp.Jobs;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ChokaQ.SampleApp.Handlers;

public class PrintMessageJobHandler : IChokaQJobHandler<PrintMessageJob>
{
    private readonly ILogger<PrintMessageJobHandler> _logger;
    private readonly IJobContext _context; // Context for progress reporting

    // Static dictionary to track failures
    private static readonly ConcurrentDictionary<string, int> _simulatedFailures = new();

    public PrintMessageJobHandler(
        ILogger<PrintMessageJobHandler> logger,
        IJobContext context)
    {
        _logger = logger;
        _context = context;
    }

    public async Task HandleAsync(PrintMessageJob job, CancellationToken ct)
    {
        // 1. Chaos Monkey Logic
        SimulateDeterministicFailure(job);

        _logger.LogInformation($"[APP HANDLER] Processing: {job.Text}");

        // Simulate progress with a loop
        // We'll increment progress by 10% every 100ms
        for (int i = 10; i <= 100; i += 10)
        {
            await Task.Delay(100, ct); // Simulate work
            await _context.ReportProgressAsync(i);
        }

        _logger.LogInformation($"[APP HANDLER] Completed: {job.Text}");

        // Cleanup memory after success
        _simulatedFailures.TryRemove(job.Id, out _);
    }

    private void SimulateDeterministicFailure(PrintMessageJob job)
    {
        var match = Regex.Match(job.Text, @"Job #(\d+)");
        if (!match.Success) return;

        if (int.TryParse(match.Groups[1].Value, out int index))
        {
            int failuresRequired = 0;
            if (index % 40 == 0) failuresRequired = 4;
            else if (index % 30 == 0) failuresRequired = 3;
            else if (index % 20 == 0) failuresRequired = 2;
            else if (index % 10 == 0) failuresRequired = 1;

            if (failuresRequired > 0)
            {
                int currentFailures = _simulatedFailures.GetOrAdd(job.Id, 0);
                if (currentFailures < failuresRequired)
                {
                    _simulatedFailures.TryUpdate(job.Id, currentFailures + 1, currentFailures);
                    _logger.LogWarning($"[SIMULATION] Job #{index} fails ({currentFailures + 1}/{failuresRequired}).");
                    throw new Exception($"Simulated Crash! (Failure {currentFailures + 1}/{failuresRequired})");
                }
            }
        }
    }
}