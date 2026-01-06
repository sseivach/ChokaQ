using ChokaQ.Abstractions;
using ChokaQ.SampleApp.Jobs;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ChokaQ.SampleApp.Handlers;

public class PrintMessageJobHandler : IChokaQJobHandler<PrintMessageJob>
{
    private readonly ILogger<PrintMessageJobHandler> _logger;

    // Static dictionary to track failures per Job ID across transient handler instances.
    private static readonly ConcurrentDictionary<string, int> _simulatedFailures = new();

    public PrintMessageJobHandler(ILogger<PrintMessageJobHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(PrintMessageJob job, CancellationToken ct)
    {
        // 1. Chaos Monkey Logic
        SimulateDeterministicFailure(job);

        // 2. Normal processing
        _logger.LogInformation($"[APP HANDLER] Processing: {job.Text}");
        await Task.Delay(500, ct);
        _logger.LogInformation($"[APP HANDLER] Completed: {job.Text}");

        // Cleanup memory after success
        _simulatedFailures.TryRemove(job.Id, out _);
    }

    /// <summary>
    /// Throws exceptions deterministically based on Job Index.
    /// MaxRetries is 3.
    /// - Job #10: Fails 1 time (Recovered)
    /// - Job #20: Fails 2 times (Recovered)
    /// - Job #30: Fails 3 times (Recovered at last attempt)
    /// - Job #40: Fails 4 times (KILLED)
    /// </summary>
    private void SimulateDeterministicFailure(PrintMessageJob job)
    {
        var match = Regex.Match(job.Text, @"Job #(\d+)");
        if (!match.Success) return;

        if (int.TryParse(match.Groups[1].Value, out int index))
        {
            int failuresRequired = 0;

            if (index % 40 == 0) failuresRequired = 4;      // Will FAIL permanently
            else if (index % 30 == 0) failuresRequired = 3; // Will survive (barely)
            else if (index % 20 == 0) failuresRequired = 2;
            else if (index % 10 == 0) failuresRequired = 1;

            if (failuresRequired > 0)
            {
                int currentFailures = _simulatedFailures.GetOrAdd(job.Id, 0);

                if (currentFailures < failuresRequired)
                {
                    _simulatedFailures.TryUpdate(job.Id, currentFailures + 1, currentFailures);

                    _logger.LogWarning($"[SIMULATION] Job #{index} is set to fail {failuresRequired} times. Current failure: {currentFailures + 1}.");

                    throw new Exception($"Simulated Crash! (Failure {currentFailures + 1}/{failuresRequired})");
                }
            }
        }
    }
}