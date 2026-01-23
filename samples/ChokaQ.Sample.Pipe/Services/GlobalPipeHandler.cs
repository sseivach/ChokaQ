using ChokaQ.Abstractions;
using System.Text.Json;

namespace ChokaQ.Sample.Pipe.Services;

public class GlobalPipeHandler : IChokaQPipeHandler
{
    private readonly ILogger<GlobalPipeHandler> _logger;

    public GlobalPipeHandler(ILogger<GlobalPipeHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(string jobType, string payload, CancellationToken ct)
    {
        // In PIPE mode, we receive the raw string key and the JSON payload.
        // We decide how to parse and process it manually.

        switch (jobType)
        {
            case "FastLog":
                // Parse manually or via JsonElement (quick and dirty)
                var log = JsonSerializer.Deserialize<JsonElement>(payload);
                var lvl = log.GetProperty("Level").GetString();
                var msg = log.GetProperty("Message").GetString();

                _logger.LogInformation("[PIPE LOG] {Level}: {Message}", lvl, msg);
                break;

            case "MetricEvent":
                // We can just log the raw payload or deserialize to a local DTO if needed
                _logger.LogWarning("📊 METRIC RECEIVED: {Payload}", payload);
                await Task.Delay(50, ct); // Simulate DB write
                break;

            case "SlowOperation":
                _logger.LogInformation("🐢 Starting heavy calculation...");
                await Task.Delay(2000, ct);
                _logger.LogInformation("✅ Calculation done.");
                break;

            default:
                _logger.LogError("Unknown Item in the Pipe: {Type}", jobType);
                break;
        }
    }
}